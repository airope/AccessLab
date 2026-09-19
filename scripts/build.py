#!/usr/bin/env python3
"""Build the real frontend into the same-origin ASP.NET static root."""
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[1]


def reject_symlinks(root, path):
    """Check lexical components before resolving; never follow output links."""
    current = root
    for component in path.relative_to(root).parts:
        current /= component
        if current.is_symlink():
            raise ValueError(f'Refusing symlink in generated build path: {current}')


def replace_static_root(root):
    """Replace only wwwroot (exclusively generated), after staging a full copy."""
    root = root.resolve()
    source = root / 'src/AccessLab.Web/dist'
    target = root / 'src/AccessLab.Api/wwwroot'
    reject_symlinks(root, source)
    reject_symlinks(root, target)
    if not source.is_dir() or not (source / 'index.html').is_file():
        raise ValueError('Frontend build is missing dist/index.html')
    for path in source.rglob('*'):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError(f'Refusing non-regular frontend output: {path}')
    with tempfile.TemporaryDirectory(prefix='.wwwroot-', dir=target.parent) as staging:
        staged = Path(staging) / 'wwwroot'
        shutil.copytree(source, staged)
        reject_symlinks(root, target)
        if target.exists():
            shutil.rmtree(target)
        staged.rename(target)


def main():
    web = ROOT / 'src/AccessLab.Web'
    reject_symlinks(ROOT, web / 'dist')
    reject_symlinks(ROOT, ROOT / 'src/AccessLab.Api/wwwroot')
    subprocess.run(['npm', 'ci'], cwd=web, check=True)
    subprocess.run(['npm', 'run', 'build'], cwd=web, check=True)
    subprocess.run(
        ['dotnet', 'build', 'src/AccessLab.Api', '--configuration', 'Release'],
        cwd=ROOT,
        check=True,
    )
    replace_static_root(ROOT)
    print('Frontend replaced in API wwwroot; restart with python3 scripts/native.py api.')


if __name__ == '__main__':
    main()
