"""Configuration behavior tests; no credentials are logged."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name('setup.py')

class SetupTests(unittest.TestCase):
    def test_private_idempotent_generation_and_exact_oidc_contract(self):
        self.assertTrue(SCRIPT.exists(), 'Private setup generator is missing')
        spec = importlib.util.spec_from_file_location('setup', SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            module.generate(root)
            private = root / '.local'
            before = (private / 'credentials.json').read_bytes()
            module.generate(root)
            self.assertEqual(before, (private / 'credentials.json').read_bytes())
            credentials = json.loads(before)
            realm = json.loads((private / 'realm/accesslab-realm.json').read_text())
            self.assertEqual(realm['realm'], 'accesslab')
            self.assertEqual(len(realm['users']), 7)
            for index, name in enumerate(['alice','bob','dana','claire','eve','frank','unknown'],1):
                user = realm['users'][index-1]
                self.assertEqual(user['id'], f'00000000-0000-0000-0000-{index:012d}')
                self.assertEqual(user['credentials'][0]['value'], credentials['users'][name]['password'])
                self.assertGreaterEqual(len(credentials['users'][name]['password']), 24)
            client = realm['clients'][0]
            self.assertFalse(client['publicClient'])
            self.assertFalse(client['directAccessGrantsEnabled'])
            self.assertEqual(client['attributes']['pkce.code.challenge.method'], 'S256')
            self.assertEqual(client['redirectUris'], ['https://localhost:5443/signin-oidc'])
            for path in private.rglob('*'):
                if path.is_file():
                    self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            self.assertEqual(private.stat().st_mode & 0o777, 0o700)

if __name__ == '__main__':
    unittest.main()
