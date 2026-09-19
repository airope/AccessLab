#!/usr/bin/env python3
"""Run real-OIDC acceptance on a fresh database, never on the demonstration data.

Stop the native API first: the exact registered OIDC callback uses port 5443.
PostgreSQL and Keycloak stay running. This command creates and owns only a unique
scratch database and its API process. Start the normal API again when it finishes.
Scratch cookie keys and logs are deleted, including on failure. --keep-failure-log
retains only a failed run's API log in a unique private .local/e2e-failures artifact.
"""
import argparse
import json
import os
from pathlib import Path
import re

import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

import native
from verify import verify_served_assets

ROOT = Path(__file__).resolve().parents[1]
URL = "https://localhost:5443"
DATABASE_NAME = re.compile(r"accesslab_e2e_[0-9a-f]{32}\Z")


def isolated_environment(source, database):
    if not DATABASE_NAME.fullmatch(database):
        raise ValueError("Only a unique runner-owned E2E database is permitted.")
    connection = source.get("ConnectionStrings__Database", "")
    entries = [part.strip() for part in connection.split(";") if part.strip()]
    databases = [part for part in entries if part.split("=", 1)[0].strip().lower() in {"database", "initial catalog"}]
    if len(databases) != 1 or databases[0].split("=", 1)[1].strip() != "accesslab":
        raise ValueError("Expected the documented native application database configuration.")
    env = dict(source)
    env["ConnectionStrings__Database"] = ";".join(
        "Database=" + database if part == databases[0] else part for part in entries
    ) + ";"
    env["AccessLab__DataProtectionPath"] = str(ROOT / ".local" / "e2e" / database / "keys")
    env["AccessLab__Seed"] = "true"
    env["AccessLab__WorkerEnabled"] = "true"
    env["ACCESSLAB_URL"] = URL
    env["ACCESSLAB_E2E_ISOLATED"] = "1"
    return env


def require_free_port():
    for host in ("127.0.0.1", "::1"):
        try:
            with socket.create_connection((host, 5443), timeout=0.5):
                pass
        except OSError:
            continue
        raise RuntimeError("Stop the native API before browser verification; port 5443 is occupied. No process was stopped.")


def validate_cluster_directory(value):
    if Path(value).resolve() != (ROOT / ".local/postgres").resolve():
        raise RuntimeError("Refusing browser setup: PostgreSQL is not the project-private cluster.")


def wait_ready(process):
    deadline = time.monotonic() + 30
    context = ssl._create_unverified_context()  # Project-local development certificate.
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Isolated API exited before readiness; use --keep-failure-log to retain diagnostics.")
        try:
            with urllib.request.urlopen(URL + "/health/ready", context=context, timeout=1) as response:
                if json.load(response).get("status") == "ready":
                    return
        except (OSError, ValueError, urllib.error.URLError):
            pass
        time.sleep(0.2)
    raise RuntimeError("Isolated API did not become ready within 30 seconds.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record-demo", action="store_true", help="Record the walkthrough after tests using the same scratch database.")
    parser.add_argument("--keep-failure-log", action="store_true", help="Retain only a failed run's API log in a unique private .local/e2e-failures artifact; always delete scratch keys and directory.")
    args = parser.parse_args()
    os.umask(0o077)
    require_free_port()
    pg_env = native.db_env()
    pg_bin = native.postgres_bin()
    actual = subprocess.check_output(
        [str(pg_bin / "psql"), "-tAc", "SHOW data_directory"], env=pg_env, text=True
    ).strip()
    validate_cluster_directory(actual)
    # Compile and publish before the temporary server exists: no stale process/bundle.
    subprocess.run(["python3", "scripts/build.py"], cwd=ROOT, check=True)
    database = "accesslab_e2e_" + uuid.uuid4().hex
    env = isolated_environment(native.config_env(), database)
    run_dir = ROOT / ".local/e2e" / database
    run_dir.mkdir(parents=True, mode=0o700)
    created = False
    process = None
    failed = True
    log_path = run_dir / "api.log"
    try:
        subprocess.run([str(pg_bin / "createdb"), database], env=pg_env, check=True)
        created = True
        with log_path.open("wb") as log:
            process = subprocess.Popen(
                ["dotnet", str(ROOT / "src/AccessLab.Api/bin/Release/net10.0/AccessLab.Api.dll")],
                cwd=ROOT / "src/AccessLab.Api", env=env, stdout=log, stderr=subprocess.STDOUT,
            )
            wait_ready(process)
            verify_served_assets(URL, ROOT / "src/AccessLab.Web/dist")
            subprocess.run(["npm", "test"], cwd=ROOT / "tests/e2e", env=env, check=True)
            if args.record_demo:
                subprocess.run(["node", "record-demo.mjs"], cwd=ROOT / "tests/e2e", env=env, check=True)
        failed = False
    finally:
        try:
            try:
                if process is not None and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
            finally:
                if created:
                    # Name was generated locally and validated; never accept a caller's DB name.
                    subprocess.run([str(pg_bin / "dropdb"), "--force", database], env=pg_env, check=True)
        except BaseException:
            failed = True
            raise
        finally:
            try:
                if failed and args.keep_failure_log and log_path.is_file():
                    artifacts = ROOT / ".local/e2e-failures"
                    artifacts.mkdir(parents=True, exist_ok=True, mode=0o700)
                    artifact = Path(tempfile.mkdtemp(prefix=database + "-", dir=artifacts))
                    retained_log = artifact / "api.log"
                    shutil.copyfile(log_path, retained_log)
                    retained_log.chmod(0o600)
                    print(f"Failed-run API log retained at {retained_log}")
            finally:
                # Only the directory successfully created by this invocation is owned.
                shutil.rmtree(run_dir)
        print("Isolated run ended; the normal demonstration database was not reset. Restart the native API.")


if __name__ == "__main__":
    main()
