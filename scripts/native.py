#!/usr/bin/env python3
"""Foreground project services: use a terminal per service; never global launch agents."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / '.local'
KEYCLOAK_VERSION = '26.7.4'
KEYCLOAK_SHA256 = '04823c336b797a7e18889a44262a7a64e6bf624cbbcc518c85e2622176ff2eee'
KC = LOCAL / f'keycloak-{KEYCLOAK_VERSION}'

def postgres_port():
    return str(json.loads((LOCAL / 'runtime.json').read_text())['postgresPort'])

def postgres_bin():
    explicit = os.environ.get('POSTGRES_BIN')
    if explicit:
        return Path(explicit)
    if Path('/opt/homebrew/opt/postgresql@17/bin/postgres').exists():
        return Path('/opt/homebrew/opt/postgresql@17/bin')
    if shutil.which('pg_config'):
        return Path(subprocess.check_output(['pg_config', '--bindir'], text=True).strip())
    raise SystemExit('Install PostgreSQL 17; set POSTGRES_BIN to its bin directory.')

def config_env():
    env = dict(os.environ)
    config = json.loads((LOCAL / 'appsettings.native.json').read_text())
    def flatten(value, prefix=''):
        for name, item in value.items():
            key = prefix + name
            if isinstance(item, dict):
                flatten(item, key + '__')
            else:
                env[key] = str(item).lower() if isinstance(item, bool) else str(item)
    flatten(config)
    env['ASPNETCORE_ENVIRONMENT'] = 'Development'
    env['ASPNETCORE_URLS'] = 'https://localhost:5443'
    return env

def db_env():
    env = dict(os.environ)
    env.update(PGPASSFILE=str(LOCAL / 'pgpass'), PGHOST='127.0.0.1', PGPORT=postgres_port(), PGUSER='accesslab', PGDATABASE='postgres')
    return env

def run(command, **kwargs):
    return subprocess.run([str(item) for item in command], cwd=ROOT, check=True, **kwargs)

def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['download-keycloak', 'init-postgres', 'postgres', 'create-databases', 'keycloak', 'api', 'test', 'ready', 'reset-fixtures'])
    args, extra = parser.parse_known_args()
    if args.action == 'download-keycloak':
        archive = LOCAL / 'downloads' / f'keycloak-{KEYCLOAK_VERSION}.tar.gz'
        archive.parent.mkdir(parents=True, exist_ok=True)
        if not archive.exists():
            urllib.request.urlretrieve(f'https://github.com/keycloak/keycloak/releases/download/{KEYCLOAK_VERSION}/{archive.name}', archive)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != KEYCLOAK_SHA256:
            raise SystemExit('Keycloak checksum mismatch; archive not extracted. Remove the archive and retry.')
        if not KC.exists():
            with tarfile.open(archive) as source:
                source.extractall(LOCAL, filter='data')
        print(f'Official Keycloak {KEYCLOAK_VERSION} archive SHA-256 verified.')
    elif args.action == 'init-postgres':
        if not (LOCAL / 'postgres/PG_VERSION').exists():
            run([postgres_bin() / 'initdb', '-D', LOCAL / 'postgres', '-U', 'accesslab', '--auth=scram-sha-256', '--pwfile', LOCAL / 'postgres-password', '--encoding=UTF8', '--locale=C'])
        else:
            print('Project PostgreSQL cluster already initialized; not reset.')
    elif args.action == 'postgres':
        os.execv(str(postgres_bin() / 'postgres'), [str(postgres_bin() / 'postgres'), '-D', str(LOCAL / 'postgres'), '-h', '127.0.0.1', '-p', postgres_port(), '-k', str(LOCAL)])
    elif args.action == 'create-databases':
        env = db_env()
        for database in ['accesslab', 'keycloak', 'accesslab_tests']:
            result = subprocess.check_output([str(postgres_bin() / 'psql'), '-tAc', f"SELECT 1 FROM pg_database WHERE datname='{database}'"], env=env, text=True).strip()
            if result != '1':
                run([postgres_bin() / 'createdb', database], env=env)
        print('Project databases ready: accesslab, keycloak, accesslab_tests.')
    elif args.action == 'keycloak':
        env = dict(os.environ)
        if not env.get('JAVA_HOME') and Path('/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home').exists():
            env['JAVA_HOME'] = '/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home'
        env.update(KC_DB='postgres', KC_DB_URL=f'jdbc:postgresql://127.0.0.1:{postgres_port()}/keycloak', KC_DB_USERNAME='accesslab', KC_DB_PASSWORD=(LOCAL / 'postgres-password').read_text().strip(), KC_HEALTH_ENABLED='true')
        imports = KC / 'data/import'
        imports.mkdir(parents=True, exist_ok=True, mode=0o700)
        shutil.copyfile(LOCAL / 'realm/accesslab-realm.json', imports / 'accesslab-realm.json')
        (imports / 'accesslab-realm.json').chmod(0o600)
        os.execve(str(KC / 'bin/kc.sh'), [str(KC / 'bin/kc.sh'), 'start-dev', '--http-host=127.0.0.1', '--http-port=58080', '--hostname=http://localhost:58080', '--http-management-host=127.0.0.1', '--import-realm'], env)
    elif args.action == 'api':
        os.chdir(ROOT)
        os.execvpe('dotnet', ['dotnet', 'run', '--no-launch-profile', '--project', 'src/AccessLab.Api', *extra], config_env())
    elif args.action == 'test':
        env = config_env()
        connection = env['ConnectionStrings__Database'].replace('Database=accesslab;', 'Database=accesslab_tests;')
        env['ACCESSLAB_TEST_DATABASE'] = connection
        env['ConnectionStrings__Database'] = connection
        run(['dotnet', 'test', *extra], env=env)
    elif args.action == 'reset-fixtures':
        if extra != ['--confirm-local-data-loss']:
            raise SystemExit('Stop the API, then pass --confirm-local-data-loss. This erases ONLY the project accesslab database.')
        env = db_env()
        actual = subprocess.check_output([str(postgres_bin() / 'psql'), '-tAc', 'SHOW data_directory'], env=env, text=True).strip()
        if Path(actual).resolve() != (LOCAL / 'postgres').resolve():
            raise SystemExit('Refusing reset: server is not this project-private PostgreSQL cluster.')
        run([postgres_bin() / 'dropdb', 'accesslab'], env=env)
        run([postgres_bin() / 'createdb', 'accesslab'], env=env)
        print('Project application DB reset. Restart API with Seed=true; Keycloak and credentials unchanged.')
    elif args.action == 'ready':
        run([postgres_bin() / 'psql', '-tAc', 'SELECT current_database(), version()'], env=db_env())
        url = 'http://localhost:58080/realms/accesslab/.well-known/openid-configuration'
        with urllib.request.urlopen(url, timeout=10) as response:
            metadata = json.load(response)
        assert metadata['issuer'] == 'http://localhost:58080/realms/accesslab'
        with urllib.request.urlopen('http://localhost:9000/health/ready', timeout=10) as response:
            assert json.load(response)['status'] == 'UP'
        print('PostgreSQL authenticated query, Keycloak issuer and readiness verified.')

if __name__ == '__main__':
    main()
