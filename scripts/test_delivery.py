"""Delivery regressions using disposable files only; no application services."""
import importlib.util
import io
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch


SCRIPTS = Path(__file__).resolve().parent


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'scripts').mkdir()
        shutil.copyfile(SCRIPTS / 'build.py', self.root / 'scripts/build.py')
        self.dist = self.root / 'src/AccessLab.Web/dist'
        self.dist.mkdir(parents=True)
        (self.dist / 'index.html').write_text('fresh')
        self.target = self.root / 'src/AccessLab.Api/wwwroot'
        self.target.mkdir(parents=True)
        (self.target / 'obsolete.js').write_text('stale')
        self.sentinel = self.target.parent / 'keep.txt'
        self.sentinel.write_text('keep')

    def build(self):
        with patch('subprocess.run'), patch('builtins.print'):
            runpy.run_path(str(self.root / 'scripts/build.py'), run_name='__main__')

    def test_replaces_only_generated_wwwroot(self):
        self.build()
        self.assertFalse((self.target / 'obsolete.js').exists())
        self.assertEqual((self.target / 'index.html').read_text(), 'fresh')
        self.assertEqual(self.sentinel.read_text(), 'keep')

    def test_rejects_destination_symlink_without_touching_target(self):
        outside = self.root / 'outside'
        self.target.rename(outside)
        self.target.symlink_to(outside, target_is_directory=True)
        with self.assertRaises((ValueError, SystemExit)):
            self.build()
        self.assertEqual((outside / 'obsolete.js').read_text(), 'stale')

    def test_rejects_symlink_in_source_without_deleting_old_build(self):
        (self.dist / 'leak.txt').symlink_to(self.sentinel)
        with self.assertRaises((ValueError, SystemExit)):
            self.build()
        self.assertTrue((self.target / 'obsolete.js').exists())

    def test_rejects_symlinked_api_parent(self):
        api = self.target.parent
        outside = self.root / 'outside'
        api.rename(outside)
        api.symlink_to(outside, target_is_directory=True)
        with self.assertRaises((ValueError, SystemExit)):
            self.build()
        self.assertEqual((outside / 'wwwroot/obsolete.js').read_text(), 'stale')

    def test_rejects_dist_symlink_before_external_build_can_empty_it(self):
        outside = self.root / 'outside'
        self.dist.rename(outside)
        self.dist.symlink_to(outside, target_is_directory=True)
        with patch('subprocess.run') as command, patch('builtins.print'):
            with self.assertRaises((ValueError, SystemExit)):
                runpy.run_path(str(self.root / 'scripts/build.py'), run_name='__main__')
        command.assert_not_called()
        self.assertEqual((outside / 'index.html').read_text(), 'fresh')


class SecretScannerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'scripts').mkdir()
        shutil.copyfile(SCRIPTS / 'check-secrets.py', self.root / 'scripts/check-secrets.py')
        subprocess.run(['git', 'init', '-q'], cwd=self.root, check=True)

    def write(self, name, value='synthetic-unrelated-password'):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value)

    def scan(self):
        return subprocess.run(
            [sys.executable, 'scripts/check-secrets.py'], cwd=self.root,
            capture_output=True, text=True,
        )

    def test_untracked_public_candidates_reject_env_variants_and_pem(self):
        for name in ('.env.production', 'nested/.env', 'nested/certificate.pem'):
            self.write(name)
        result = self.scan()
        self.assertNotEqual(result.returncode, 0)
        for name in ('.env.production', 'nested/.env', 'nested/certificate.pem'):
            self.assertIn(name, result.stderr)
        self.assertNotIn('synthetic-unrelated-password', result.stdout + result.stderr)

    def test_tracked_private_paths_are_not_skipped_by_walk_exclusions(self):
        self.write('bin/.env.stage')
        self.write('dist/certificate.pem')
        self.write('node_modules/leak.txt', '-----BEGIN ' + 'PRIVATE KEY-----')
        self.write('.local/private.txt')
        subprocess.run(['git', 'add', '-f', '.'], cwd=self.root, check=True)
        result = self.scan()
        self.assertNotEqual(result.returncode, 0)
        for name in ('bin/.env.stage', 'dist/certificate.pem', 'node_modules/leak.txt', '.local/private.txt'):
            self.assertIn(name, result.stderr)
        self.assertNotIn('-----BEGIN', result.stdout + result.stderr)

    def test_example_and_untracked_private_runtime_are_allowed(self):
        self.write('.env.example', 'EXAMPLE=placeholder')
        self.write('.local/private.txt')
        result = self.scan()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_public_symlinks_are_rejected_without_reading_target(self):
        self.write('.local/private.txt')
        (self.root / 'public.txt').symlink_to(self.root / '.local/private.txt')
        result = self.scan()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('public.txt', result.stderr)
        self.assertNotIn('synthetic-unrelated-password', result.stdout + result.stderr)


class DeliveryConfigurationTests(unittest.TestCase):
    def test_container_restores_locked_then_publishes_without_restore(self):
        dockerfile = (SCRIPTS.parent / 'Dockerfile').read_text()
        restore = 'RUN dotnet restore src/AccessLab.Api/AccessLab.Api.csproj --locked-mode'
        self.assertIn(restore, dockerfile)
        publish = next(line for line in dockerfile.splitlines() if line.startswith('RUN dotnet publish'))
        self.assertIn('--no-restore', publish)
        self.assertLess(dockerfile.index(restore), dockerfile.index(publish))

    def test_ci_restore_is_locked_and_all_infrastructure_tests_run(self):
        workflow = (SCRIPTS.parent / '.github/workflows/ci.yaml').read_text()
        self.assertIn('dotnet restore --locked-mode', workflow)
        test_command = next(line for line in workflow.splitlines() if 'scripts/native.py test' in line)
        self.assertIn('--no-restore', test_command)
        self.assertIn("python3 -m unittest discover -s scripts -p 'test_*.py'", workflow)
        props = ET.parse(SCRIPTS.parent / 'Directory.Build.props')
        locked = props.find('.//RestoreLockedMode')
        self.assertIsNotNone(locked, 'CI-wide locked mode must cover implicit restores too')
        self.assertEqual(locked.text, 'true')
        self.assertEqual(locked.attrib.get('Condition'), "'$(CI)' == 'true'")

    def test_generated_wwwroot_is_not_a_docker_context_input(self):
        patterns = (SCRIPTS.parent / '.dockerignore').read_text().splitlines()
        self.assertIn('src/AccessLab.Api/wwwroot', patterns)


class VerificationTests(unittest.TestCase):
    def setUp(self):
        self.verify = load_script('verify.py')
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dist = Path(self.temp.name)
        (self.dist / 'index.html').write_bytes(b'fresh index')
        (self.dist / 'app.js').write_bytes(b'fresh javascript')

    def test_stale_asset_fails_with_restart_instruction(self):
        with patch('urllib.request.urlopen', return_value=io.BytesIO(b'stale')):
            checker = getattr(self.verify, 'verify_served_assets', None)
            self.assertIsNotNone(checker, 'Missing fresh-build served-asset gate')
            with self.assertRaisesRegex(RuntimeError, 'restart'):
                checker('https://localhost:5443', self.dist)

    def test_matching_build_checks_every_file_and_root(self):
        requested = []

        def serve(request, **kwargs):
            requested.append(request.full_url)
            name = request.full_url.rsplit('/', 1)[1] or 'index.html'
            return io.BytesIO((self.dist / name).read_bytes())

        checker = getattr(self.verify, 'verify_served_assets', None)
        self.assertIsNotNone(checker, 'Missing fresh-build served-asset gate')
        with patch('urllib.request.urlopen', side_effect=serve):
            checker('https://localhost:5443', self.dist)
        self.assertEqual(set(requested), {
            'https://localhost:5443/',
            'https://localhost:5443/index.html',
            'https://localhost:5443/app.js',
        })

    def test_browser_routes_to_owned_fresh_backend_not_existing_app(self):
        calls = []
        with patch('sys.argv', ['verify.py', '--browser']), patch.object(
            self.verify, 'run', side_effect=lambda command, *args: calls.append(command)
        ), patch('builtins.print'):
            self.verify.main()
        self.assertIn(['python3', 'scripts/e2e.py'], calls)
        self.assertEqual(calls.count(['npm', 'test']), 1, 'Only frontend unit tests run directly')


if __name__ == '__main__':
    unittest.main()