# Local operation runbook

## Safety before starting

Use this repository only. Do not source another project's `.env`, reuse organization credentials, start global database services, or terminate another process to claim a port. All identities are fictitious. Back up anything you intend to retain before using a reset procedure.

Supported reference tools: Python 3.11+; .NET SDK 10.0.400; Node 26.5.0/npm; PostgreSQL 17; Java 21. Docker Compose v2 is optional for the native route. Keycloak 26.7.4 is pinned and its official tarball checksum is verified by `scripts/native.py` before extraction. Archive digest comes from the official GitHub release asset metadata.

## Native development (no Docker)

On Apple Silicon macOS, inspect/install the binaries without registering services:

```sh
brew list --versions postgresql@17 openjdk@21
# Only if missing:
brew install postgresql@17 openjdk@21
/opt/homebrew/opt/openjdk@21/bin/java -version
```

Homebrew may initialize its default cluster during package installation; **do not start it**. AccessLab initializes and launches a separate cluster inside `.local/postgres`. On another OS set `POSTGRES_BIN` to your PostgreSQL `bin` directory and `JAVA_HOME` to a Java 21 installation.

From the repository root:

```sh
python3 scripts/setup.py --certificate
# If port 55432 is occupied, explicitly choose another BEFORE starting:
# python3 scripts/setup.py --certificate --postgres-port 55433
python3 scripts/native.py init-postgres
python3 scripts/native.py download-keycloak
```

Start PostgreSQL in a dedicated terminal (foreground, Ctrl-C to stop):

```sh
python3 scripts/native.py postgres
```

In another terminal, once the listener is ready:

```sh
python3 scripts/native.py create-databases
python3 scripts/native.py keycloak
```

That second command remains in the foreground. In a third terminal verify dependencies, build and launch the application:

```sh
python3 scripts/native.py ready
python3 scripts/build.py
python3 scripts/native.py api
```

The setup generator is idempotent: it preserves generated credentials and the selected native port. Keycloak imports a realm only if it does not already exist; editing the import file is not a password rotation mechanism. The app launcher exports private settings without sourcing a shell file. It sets Development, explicit fixture seed, worker enabled and demo connector controls. Backend startup applies its EF migrations for this local seeded profile. Outside local Development, deployment and migration procedures are not provided by this runbook.

Visit https://localhost:5443. The frontend build is served by the backend; Vite is not required for acceptance. For frontend-only iteration, the Vite proxy forwards `/api` and `/auth` to the HTTPS backend, but real OIDC callbacks and final acceptance use the same-origin application URL.

### Private files and permissions

| Path | Content / handling |
|---|---|
| `.local/credentials.json` | `users.<name>.password`, database password, OIDC secret and certificate password; never publish |
| `.local/runtime.json` | Selected native PostgreSQL port |
| `.local/appsettings.native.json` | Native backend settings; launcher reads privately |
| `.local/appsettings.container.json` | Container backend settings; mounted read-only |
| `.local/realm/accesslab-realm.json` | Confidential client plus fixed-subject fixture users/passwords |
| `.local/postgres-password`, `.local/pgpass` | Database tools' private password inputs |
| `.local/certs/accesslab.pfx` | Development HTTPS certificate, private and password protected |
| `.local/data-protection/` | ASP.NET cookie protection keys; retain across ordinary restart |
| `.local/postgres/`, `.local/keycloak-26.7.4/` | Local runtime/data, never version-controlled |

The directory is mode 0700, generated credential-bearing files mode 0600. Avoid shell tracing (`set -x`), environment dumps, HTTP token logs and browser traces containing login credentials. The configuration test uses a temporary isolated directory and never prints secret values.

## Compose route

```sh
python3 scripts/setup.py --certificate
docker compose up -d --build --wait --wait-timeout 300
docker compose ps
curl --insecure --fail https://localhost:5443/health/ready
```

Only loopback ports are published. Default PostgreSQL host port is 55432; use `ACCESSLAB_POSTGRES_PORT=55433 docker compose ...` consistently if necessary. If using host integration tests against that override, also run setup with `--postgres-port 55433`. Do not run native and container Keycloak/API on the same ports simultaneously.

The API and Keycloak share a network namespace to keep the exact `localhost:58080` issuer accessible to the browser and BFF. Container start dependencies are health-gated. The app image builds React first, publishes .NET second and copies the frontend into `wwwroot`. No secret goes into the image.

Compose is a **local Development** profile, not a production manifest. Containers can read restrictive host-owned secret files by running as root; do not treat that as public-deployment hardening. Local service startup does not create a Keycloak bootstrap admin account because fixture import does not require one.

Normal stop, retaining state:

```sh
docker compose down
```

### Health and troubleshooting

- `python3 scripts/native.py ready`: authenticated SQL plus exact OIDC discovery issuer plus Keycloak UP readiness.
- `https://localhost:5443/health/live`: process health; `.../health/ready`: backend dependency readiness. Use `curl --insecure` only for this local development certificate.
- Login failure: check exact issuer, redirect URI, client secret agreement, certificate and browser time. Do not disable signature, issuer, state, nonce or CSRF validation to make a test pass.
- Port conflict: inspect `lsof -nP -iTCP:55432 -sTCP:LISTEN`, select another native port and rerun setup; never touch unrelated data or processes.
- Password mismatch after deleting only credentials: existing PostgreSQL/Keycloak databases retain old credentials. Restore the private credentials backup or perform an explicitly destructive complete reset of **this project's** state; regeneration alone does not rotate server passwords.
- Login returns 403 for Unknown: expected. No just-in-time account provisioning is enabled.
- Local logout followed by automatic Keycloak login: expected IdP SSO boundary. Choose **Use another account**, then Keycloak's **Restart login** action to enter another username; separate contexts are useful for simultaneous roles.
- Approved but resource unavailable: inspect desired versus observed state and operations; approval is not application.
- Failed connector: as Claire disable demo failure mode and request retry. Never repair authorization by directly editing observed rows.
- Database unavailable: report dependency failure; never switch to in-memory persistence or permit access on error.

## Tests and CI

`python3 scripts/native.py test --configuration Release` supplies the separate `accesslab_tests` connection through `ACCESSLAB_TEST_DATABASE`; tests must really connect. Run frontend tests with `npm --prefix src/AccessLab.Web test`. Real OIDC browser tests live in `tests/e2e` and read `.local/credentials.json` directly without logging it. Install Chromium and set `ACCESSLAB_BROWSER=chromium`, or use the installed Chrome default.

The GitHub workflow generates fresh ephemeral credentials, starts real Compose PostgreSQL, runs xUnit and PostgreSQL integration, frontend build/tests, dependency and private-material checks, then builds the full stack and runs Playwright against real Keycloak. Cleanup removes its disposable named volumes. It does not upload private configuration, IdP logs or credential-bearing traces. A checked-in workflow is not a hosted CI pass; record actual outcomes in VALIDATION.md.

For native browser acceptance, stop only the normal API and run `python3 scripts/e2e.py`, leaving PostgreSQL and Keycloak running. **Port 5443 must be free** for the registered OIDC callback. The runner verifies the project-private PostgreSQL cluster, refuses an occupied API port, creates a unique scratch database and separate cookie-protection keys, builds the current application, starts its own API and runs the real-OIDC tests. Cleanup stops only that owned process and drops only that generated database. It never drops or recreates `accesslab`. Then restart `python3 scripts/native.py api` to resume the demonstration. This is an exclusive maintenance interval, not concurrent testing against a live user's session. For a fresh video, pass `--record-demo`; the recording also uses the scratch database.

Do not set `ACCESSLAB_E2E_ISOLATED=1` against your ordinary demonstration merely to bypass the test guard. CI may set it because its Compose database and volumes are disposable. A failed test should report and clean up its own request/membership, not remove whichever active membership happens to appear first.

The native runner deletes its scratch cookie keys and logs on completion, including failure paths. For an authorized debugging run, `--keep-failure-log` retains only a failed run's API log in a unique private `.local/e2e-failures/` directory; it is not a public CI artifact. Do not attach that log to an issue without reviewing it for sensitive content.

## Reset fixtures — destructive, opt-in

Native: stop the API (leave PostgreSQL running), then:

```sh
python3 scripts/native.py reset-fixtures --confirm-local-data-loss
python3 scripts/native.py api
```

The reset refuses a server whose data directory is not this project's `.local/postgres`. It drops and recreates only the `accesslab` application database; Keycloak identities, passwords and its database remain unchanged. Active database connections cause reset to fail rather than forcibly terminate unrelated sessions. Restart with the explicit local seed profile to rebuild migrations/fixtures. Account termination is not reversible; reset is for disposable demo data only.

Compose full local reset: **after deliberately accepting loss of all AccessLab database state and sessions**, use `docker compose down --volumes`, then `docker compose up -d --build --wait --wait-timeout 300`. Keep `.local` credentials unchanged so the new realm and databases agree. Do not apply these commands to a shared or production deployment.

Stop native foreground processes in order: API, Keycloak, PostgreSQL. If an automation runner owns their handles, stop those specific handles rather than killing all Java or PostgreSQL processes. Neither native setup nor this runbook uses `brew services start`.

## Deployment boundary

These commands operate a local Development laboratory, not an internet-facing service. Public deployment is outside this runbook's supported scope. The local simulator and application audit do not prove external revocation or immutable compliance records. See the [security model](SECURITY.md) for trust boundaries and [validation record](VALIDATION.md) for the routes actually exercised.
