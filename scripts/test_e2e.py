"""Safety contracts for the isolated native browser runner; no services needed."""

from contextlib import ExitStack
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import e2e


class BrowserIsolationTests(unittest.TestCase):
    def test_only_fresh_run_database_names_are_allowed(self):
        for name in ("accesslab", "accesslab_tests", "postgres", "accesslab_e2e_x;DROP"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                e2e.isolated_environment({"ConnectionStrings__Database": "Host=localhost;Database=accesslab;"}, name)

    def test_application_database_is_replaced_and_cookie_keys_are_isolated(self):
        name = "accesslab_e2e_" + "a" * 32
        source = {"ConnectionStrings__Database": "Host=localhost;Database=accesslab;", "AccessLab__DataProtectionPath": "original"}
        env = e2e.isolated_environment(source, name)
        self.assertEqual(env["ConnectionStrings__Database"], f"Host=localhost;Database={name};")
        self.assertEqual(source["ConnectionStrings__Database"], "Host=localhost;Database=accesslab;")
        self.assertNotEqual(env["AccessLab__DataProtectionPath"], source["AccessLab__DataProtectionPath"])
        self.assertEqual(env["ACCESSLAB_E2E_ISOLATED"], "1")

    def test_unexpected_database_configuration_fails_closed(self):
        name = "accesslab_e2e_" + "b" * 32
        for value in ("Host=localhost;Database=other;", "Host=localhost;", "Database=accesslab;Database=other;"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                e2e.isolated_environment({"ConnectionStrings__Database": value}, name)

    def test_occupied_port_is_never_taken_over(self):
        with patch("e2e.socket.create_connection") as connect:
            connect.return_value.__enter__.return_value = object()
            with self.assertRaisesRegex(RuntimeError, "Stop the native API"):
                e2e.require_free_port()

    def test_foreign_database_cluster_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "project-private"):
            e2e.validate_cluster_directory("/unrelated/database")


class BrowserLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.root = Path(self.stack.enter_context(tempfile.TemporaryDirectory()))
        self.database = "accesslab_e2e_" + "c" * 32
        self.run_dir = self.root / ".local/e2e" / self.database
        self.neighbor = self.run_dir.parent / "other-run" / "keys"
        self.neighbor.mkdir(parents=True)
        (self.neighbor / "sentinel").write_text("untouched fixture")
        self.output = io.StringIO()
        self.stack.enter_context(patch("sys.stdout", self.output))
        self.stack.enter_context(patch("sys.argv", ["e2e.py"]))
        self.stack.enter_context(patch("e2e.ROOT", self.root))
        self.stack.enter_context(patch("e2e.os.umask"))
        self.stack.enter_context(patch("e2e.uuid.uuid4", return_value=Mock(hex="c" * 32)))
        self.stack.enter_context(patch("e2e.require_free_port"))
        self.stack.enter_context(patch("e2e.native.db_env", return_value={}))
        self.stack.enter_context(patch("e2e.native.postgres_bin", return_value=Path("/fake/pg")))
        self.stack.enter_context(patch("e2e.native.config_env", return_value={
            "ConnectionStrings__Database": "Host=localhost;Database=accesslab;"}))
        self.stack.enter_context(patch("e2e.subprocess.check_output", return_value=str(self.root / ".local/postgres")))
        self.run = self.stack.enter_context(patch("e2e.subprocess.run"))
        self.process = Mock()
        self.process.poll.return_value = None
        self.popen = self.stack.enter_context(patch("e2e.subprocess.Popen", side_effect=self.start_api))
        self.real_ready = e2e.wait_ready
        self.ready = self.stack.enter_context(patch("e2e.wait_ready"))
        self.stack.enter_context(patch("e2e.verify_served_assets"))

    def start_api(self, *args, **kwargs):
        keys = Path(kwargs["env"]["AccessLab__DataProtectionPath"])
        keys.mkdir()
        (keys / "fixture-key").write_text("synthetic fixture only")
        kwargs["stdout"].write(b"synthetic API log\n")
        return self.process

    def assert_clean(self):
        self.assertFalse(self.run_dir.exists(), "owned scratch directory must be removed")
        self.assertEqual((self.neighbor / "sentinel").read_text(), "untouched fixture")

    def test_success_removes_only_owned_run_directory(self):
        e2e.main()
        self.assert_clean()
        self.process.terminate.assert_called_once_with()
        self.process.wait.assert_called_once_with(timeout=10)
        self.run.assert_any_call(["/fake/pg/dropdb", "--force", self.database], env={}, check=True)
        self.assertFalse((self.root / ".local/e2e-failures").exists())

    def test_failed_run_explicitly_retains_only_log_in_private_unique_artifact(self):
        self.stack.enter_context(patch("sys.argv", ["e2e.py", "--keep-failure-log"]))
        self.ready.side_effect = RuntimeError("fixture readiness failure")
        for _ in range(2):
            with self.assertRaisesRegex(RuntimeError, "fixture readiness failure"):
                e2e.main()
            self.assert_clean()
        artifacts = list((self.root / ".local/e2e-failures").iterdir())
        self.assertEqual(len(artifacts), 2)
        for artifact in artifacts:
            self.assertEqual(artifact.stat().st_mode & 0o777, 0o700)
            self.assertEqual([p.name for p in artifact.iterdir()], ["api.log"])
            log = artifact / "api.log"
            self.assertEqual(log.stat().st_mode & 0o777, 0o600)
            self.assertEqual(log.read_bytes(), b"synthetic API log\n")
            self.assertIn(str(log), self.output.getvalue())

    def fail_command(self, executable):
        def run(command, **kwargs):
            if Path(command[0]).name == executable:
                raise subprocess.CalledProcessError(1, command)
        self.run.side_effect = run

    def test_child_exit_cleans_scratch_without_stopping_exited_child(self):
        self.process.poll.return_value = 1
        self.ready.side_effect = self.real_ready
        with patch("e2e.urllib.request.urlopen") as request:
            with self.assertRaisesRegex(RuntimeError, "use --keep-failure-log") as error:
                e2e.main()
            request.assert_not_called()
        self.assertNotIn("inspect", str(error.exception))
        self.assert_clean()
        self.process.terminate.assert_not_called()
        self.process.kill.assert_not_called()
        self.assertFalse((self.root / ".local/e2e-failures").exists())

    def test_build_failure_creates_no_scratch_database_or_child(self):
        self.fail_command("python3")
        with self.assertRaises(subprocess.CalledProcessError):
            e2e.main()
        self.assert_clean()
        self.popen.assert_not_called()
        self.assertEqual(self.run.call_count, 1)

    def test_drop_failure_still_removes_scratch(self):
        self.fail_command("dropdb")
        with self.assertRaises(subprocess.CalledProcessError):
            e2e.main()
        self.assert_clean()
        self.process.terminate.assert_called_once_with()
        self.assertFalse((self.root / ".local/e2e-failures").exists())

    def test_drop_failure_can_explicitly_retain_log(self):
        self.stack.enter_context(patch("sys.argv", ["e2e.py", "--keep-failure-log"]))
        self.fail_command("dropdb")
        with self.assertRaises(subprocess.CalledProcessError):
            e2e.main()
        self.assert_clean()
        self.assertEqual(len(list((self.root / ".local/e2e-failures").glob("*/api.log"))), 1)

    def test_success_with_retention_flag_keeps_no_log(self):
        self.stack.enter_context(patch("sys.argv", ["e2e.py", "--keep-failure-log"]))
        e2e.main()
        self.assert_clean()
        self.assertFalse((self.root / ".local/e2e-failures").exists())

    def test_createdb_failure_cleans_scratch_without_dropping_database(self):
        self.fail_command("createdb")
        with self.assertRaises(subprocess.CalledProcessError):
            e2e.main()
        self.assert_clean()
        self.popen.assert_not_called()
        self.assertEqual([Path(call.args[0][0]).name for call in self.run.call_args_list],
                         ["python3", "createdb"])

    def test_existing_run_directory_is_never_adopted_or_deleted(self):
        self.run_dir.mkdir()
        sentinel = self.run_dir / "preexisting"
        sentinel.write_text("not owned")
        with self.assertRaises(FileExistsError):
            e2e.main()
        self.assertEqual(sentinel.read_text(), "not owned")
        self.popen.assert_not_called()
        self.assertEqual(self.run.call_count, 1)

    def test_log_copy_failure_still_removes_scratch_keys_and_directory(self):
        self.stack.enter_context(patch("sys.argv", ["e2e.py", "--keep-failure-log"]))
        self.ready.side_effect = RuntimeError("fixture failure")
        with patch("e2e.shutil.copyfile", side_effect=OSError("fixture copy failure")):
            with self.assertRaisesRegex(OSError, "fixture copy failure"):
                e2e.main()
        self.assert_clean()


if __name__ == "__main__":
    unittest.main()
