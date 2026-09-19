#!/usr/bin/env python3
"""Run the local verification gates without hiding failed exit codes.

Start PostgreSQL and Keycloak before --browser; leave API port 5443 free.
Browser verification uses e2e.py's freshly started backend and isolated database,
not an assumed-current running API. No demo reset or service installation occurs.
"""
import argparse
from pathlib import Path
import ssl
import subprocess
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def run(command, cwd=ROOT):
    print('VERIFY:', ' '.join(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True)


def verify_served_assets(base_url, dist):
    """Fail closed unless every fresh frontend file matches the served bytes.

    Call after building and restarting the backend, before Playwright. This proves
    frontend identity only: the caller MUST own a fresh backend process too.
    Only loopback HTTPS accepts the disposable development certificate.
    """
    dist = Path(dist)
    if not dist.is_dir() or not (dist / 'index.html').is_file():
        raise RuntimeError('Fresh frontend dist/index.html is missing; build and restart the API')
    url = urllib.parse.urlsplit(base_url)
    if url.scheme not in {'http', 'https'} or not url.hostname or url.query or url.fragment:
        raise ValueError('Expected an HTTP(S) application base URL without query or fragment')
    context = ssl.create_default_context()
    if url.scheme == 'https' and url.hostname in {'localhost', '127.0.0.1', '::1'}:
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
    files = sorted(path for path in dist.rglob('*') if path.is_file())
    if dist.is_symlink() or any(path.is_symlink() for path in dist.rglob('*')):
        raise RuntimeError('Refusing symlink in frontend build; rebuild and restart the API')
    requests = [(path.relative_to(dist).as_posix(), path) for path in files]
    requests.append(('', dist / 'index.html'))
    for relative, path in requests:
        request = urllib.request.Request(
            base_url.rstrip('/') + '/' + urllib.parse.quote(relative),
            headers={'Cache-Control': 'no-cache', 'Accept-Encoding': 'identity'},
        )
        expected = path.read_bytes()
        try:
            with urllib.request.urlopen(request, context=context, timeout=15) as response:
                actual = response.read(len(expected) + 1)
        except (OSError, urllib.error.URLError):
            raise RuntimeError(
                f'Cannot verify served asset {relative or "/"}; rebuild and restart the API'
            ) from None
        if actual != expected:
            raise RuntimeError(
                f'Served asset differs from fresh build: {relative or "/"}; '
                'rebuild and restart the API before browser verification'
            )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        '--browser', action='store_true',
        help='Exercise real OIDC with a fresh owned backend and isolated database.',
    )
    args = parser.parse_args()
    run(['python3', '-m', 'unittest', 'discover', '-s', 'scripts', '-p', 'test_*.py'])
    run(['python3', 'scripts/check-secrets.py'])
    run(['python3', 'scripts/native.py', 'ready'])
    run([
        'python3', 'scripts/native.py', 'test', 'tests/AccessLab.Api.Tests',
        '--configuration', 'Release', '--logger', 'trx',
    ])
    web = ROOT / 'src/AccessLab.Web'
    commands = (
        ['npm', 'test'],
        ['npm', 'run', 'format:check'],
        ['npm', 'run', 'build'],
        ['npm', 'audit', '--audit-level=high'],
    )
    for command in commands:
        run(command, web)
    run(['npx', '--no-install', 'tsc', '--noEmit'], ROOT / 'tests/e2e')
    if args.browser:
        run(['python3', 'scripts/e2e.py'])
    print('Requested verification gates passed. See test reports for exact scope; this is not a hosted CI result.')


if __name__ == '__main__':
    main()
