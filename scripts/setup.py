#!/usr/bin/env python3
"""Generate project-local credentials. Never read an unrelated .env or print secrets."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
USERS = ['alice', 'bob', 'dana', 'claire', 'eve', 'frank', 'unknown']

def private_write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(value)
    path.chmod(0o600)

def generate(root, postgres_port=None):
    os.umask(0o077)
    local = root / '.local'
    local.mkdir(mode=0o700, exist_ok=True)
    local.chmod(0o700)
    for folder in ['realm', 'certs', 'logs', 'data-protection', 'downloads']:
        (local / folder).mkdir(mode=0o700, exist_ok=True)
    runtime_path = local / 'runtime.json'
    runtime = json.loads(runtime_path.read_text()) if runtime_path.exists() else {'postgresPort': 55432}
    if postgres_port is not None:
        runtime['postgresPort'] = postgres_port
    port = runtime['postgresPort']
    private_write(runtime_path, json.dumps(runtime) + '\n')
    credentials_path = local / 'credentials.json'
    if credentials_path.exists():
        credentials = json.loads(credentials_path.read_text())
    else:
        credentials = {
            'users': {name: {'password': secrets.token_urlsafe(24)} for name in USERS},
            'database': {'password': secrets.token_hex(24)},
            'oidc': {'clientSecret': secrets.token_hex(32)},
            'certificate': {'password': secrets.token_hex(24)}
        }
        private_write(credentials_path, json.dumps(credentials, indent=2) + '\n')
    realm = {
        'realm': 'accesslab', 'enabled': True, 'registrationAllowed': False,
        'resetPasswordAllowed': False, 'loginWithEmailAllowed': False,
        'sslRequired': 'none', 'bruteForceProtected': True,
        'clients': [{
            'clientId': 'accesslab-web', 'enabled': True,
            'protocol': 'openid-connect', 'publicClient': False,
            'secret': credentials['oidc']['clientSecret'],
            'standardFlowEnabled': True, 'directAccessGrantsEnabled': False,
            'serviceAccountsEnabled': False,
            'redirectUris': ['https://localhost:5443/signin-oidc'],
            'webOrigins': ['https://localhost:5443'],
            'attributes': {'pkce.code.challenge.method': 'S256',
                'post.logout.redirect.uris': 'https://localhost:5443/signout-callback-oidc'},
            'defaultClientScopes': ['web-origins', 'acr', 'profile', 'email', 'roles']
        }],
        'users': [{
            'id': f'00000000-0000-0000-0000-{index:012d}', 'username': name,
            'enabled': True, 'emailVerified': True, 'firstName': name.capitalize(),
            'lastName': 'Demo', 'email': f'{name}@accesslab.invalid',
            'requiredActions': [],
            'credentials': [{'type': 'password', 'value': credentials['users'][name]['password'], 'temporary': False}]
        } for index, name in enumerate(USERS, 1)]
    }
    private_write(local / 'realm/accesslab-realm.json', json.dumps(realm, indent=2) + '\n')
    password = credentials['database']['password']
    private_write(local / 'postgres-password', password + '\n')
    private_write(local / 'pgpass', f'127.0.0.1:{port}:*:accesslab:{password}\n')
    for container in [False, True]:
        base = '/run/private' if container else str(local)
        config = {
            'ConnectionStrings': {'Database': f'Host={"postgres" if container else "127.0.0.1"};Port={5432 if container else port};Database=accesslab;Username=accesslab;Password={password};Include Error Detail=false'},
            'Oidc': {'Authority': 'http://localhost:58080/realms/accesslab', 'ClientId': 'accesslab-web', 'ClientSecret': credentials['oidc']['clientSecret']},
            'Kestrel': {'Certificates': {'Default': {'Path': base + '/certs/accesslab.pfx', 'Password': credentials['certificate']['password']}}},
            'AccessLab': {'DemoMode': True, 'Seed': True, 'WorkerEnabled': True, 'DataProtectionPath': '/app/data-protection' if container else str(local / 'data-protection')}
        }
        private_write(local / ('appsettings.container.json' if container else 'appsettings.native.json'), json.dumps(config, indent=2) + '\n')
    return credentials

if __name__ == '__main__':
    port = int(sys.argv[sys.argv.index('--postgres-port') + 1]) if '--postgres-port' in sys.argv else None
    values = generate(ROOT, port)
    if '--certificate' in sys.argv:
        cert = ROOT / '.local/certs/accesslab.pfx'
        if not cert.exists():
            # Password passed only to this local child, never printed. No trust store changes.
            result = subprocess.run(['dotnet', 'dev-certs', 'https', '--export-path', str(cert), '--password', values['certificate']['password']], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if result.returncode:
                raise SystemExit('Development certificate export failed; run dotnet dev-certs https --check (no secret output).')
        cert.chmod(0o600)
    print('Private configuration ready under .local/ (credentials not printed).')
