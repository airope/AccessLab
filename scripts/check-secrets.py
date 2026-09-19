#!/usr/bin/env python3
"""Fail without echoing values when private files/secrets enter public source."""
import json
import os
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {
    '.git', '.local', 'node_modules', 'bin', 'obj', 'dist', 'wwwroot',
    '__pycache__', 'playwright-report', 'test-results',
}
PRIVATE_KEY = re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----')


def private_filename(name):
    path = Path(name)
    return (
        '.local' in path.parts
        or path.name == '.env'
        or (path.name.startswith('.env.') and path.name != '.env.example')
        or path.suffix.lower() in {'.pfx', '.p12', '.pem', '.key'}
    )


def has_symlink(root, path):
    current = root
    for component in path.relative_to(root).parts:
        current /= component
        if current.is_symlink():
            return True
    return False


def public_candidates(root):
    """Include candidates before the first commit, even Git-ignored filenames."""
    for directory, dirs, files in os.walk(root):
        for name in dirs:
            path = Path(directory) / name
            if name not in EXCLUDED and path.is_symlink():
                yield path.relative_to(root).as_posix()
        dirs[:] = [name for name in dirs if name not in EXCLUDED]
        for name in files:
            yield (Path(directory) / name).relative_to(root).as_posix()


def generated_secrets(root):
    secrets = []
    private = root / '.local/credentials.json'
    if has_symlink(root, private):
        raise ValueError('Cannot safely scan .local/credentials.json')
    if not private.exists():
        return secrets

    def collect(value):
        if isinstance(value, dict):
            for item in value.values():
                collect(item)
        elif isinstance(value, list):
            for item in value:
                collect(item)
        elif isinstance(value, str) and len(value) >= 20:
            secrets.append(value.encode())

    try:
        collect(json.loads(private.read_text()))
    except (OSError, ValueError):
        raise ValueError('Cannot scan .local/credentials.json') from None
    return secrets


def scan(root):
    result = subprocess.run(
        ['git', 'ls-files', '-z'], cwd=root, capture_output=True, check=True,
    )
    tracked = {os.fsdecode(name) for name in result.stdout.split(b'\0') if name}
    # Walk exclusions apply only to untracked generated/runtime files, never the index.
    candidates = tracked | set(public_candidates(root))
    secrets = generated_secrets(root)
    errors = []
    for name in sorted(candidates):
        path = root / name
        if private_filename(name) or has_symlink(root, path):
            errors.append(name)
            continue
        try:
            data = path.read_bytes()
        except OSError:
            # Missing/unreadable tracked material is not silently considered clean.
            errors.append(name)
            continue
        if any(value in data for value in secrets) or PRIVATE_KEY.search(data):
            errors.append(name)
    return errors


def main():
    try:
        errors = scan(ROOT)
    except (ValueError, subprocess.CalledProcessError):
        raise SystemExit('Public source scan could not complete safely') from None
    if errors:
        raise SystemExit('Private or unreadable material in public paths: ' + ', '.join(errors))
    print(
        'Public source scan passed (filename policy, generated secrets and private-key markers); '
        'not a complete historical secret audit.'
    )


if __name__ == '__main__':
    main()
