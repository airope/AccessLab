# Verification evidence

## Reproduce the checks

The [GitHub Actions workflow](https://github.com/airope/AccessLab/actions/workflows/ci.yaml)
is the live, commit-specific record for the container route. A workflow definition
alone is not proof that its jobs passed. See [CONTRIBUTING.md](../CONTRIBUTING.md)
and [RUNBOOK.md](RUNBOOK.md) for local commands and service prerequisites.

## Native baseline — 19 September 2026

`python3 scripts/verify.py --browser` completed successfully against native
PostgreSQL and real Keycloak, using a freshly built API and an isolated browser-test
database. The application/test sources in this baseline are retained in the
initial public edition; publication documentation and CI configuration are
verified separately.

| Suite or gate | Observed result |
|---|---|
| Backend HTTP/domain/PostgreSQL | 87 passed, none failed or skipped |
| React component/helper tests | 33 passed across 8 files |
| Python setup/delivery/runner tests | 31 passed |
| Browser cleanup helper tests | 20 passed |
| Chrome with real OIDC | 12 scenarios passed |
| Release build | 0 errors, 0 warnings |
| TypeScript, Prettier and CSharpier | Passed |
| Locked NuGet restore | Passed |
| npm and NuGet advisory checks | No known vulnerabilities returned at the recorded check |
| Public-material scanner | Passed its filename, generated-secret and private-key checks |

## What the tests cover

The browser suite exercises identity mapping, unknown-account denial, project
scopes, independent approval, preview conflicts, idempotency, connector failure
and recovery, retained alternative access paths, account switching with SSO
cookies, CSRF rejection, inert persisted text and keyboard-only workflows.
Operational screens are checked at desktop and narrow mobile widths. Axe found
no serious or critical issue in those states; this is not full accessibility
certification.

Backend tests use controlled clocks and fault barriers for expiration, concurrent
decisions, rollback, response loss, worker/destination/acknowledgement failures,
monotone revisions, immutable decision context and consistent authorization
snapshots. Scoped pagination, account validity and preview-complexity bounds have
explicit regression coverage. [COVERAGE.md](COVERAGE.md) links requirements to code
and tests.

Native browser acceptance compares freshly built frontend files with served bytes
and starts its own Release backend. It refuses the occupied callback port and
uses a unique scratch database, not existing demonstration decisions. Its keys
and files are removed on success and failure. Test authentication is confined to
the backend test assembly; real browser acceptance uses Keycloak.

## Boundaries

Dependency advisories are point-in-time results. The source scanner is not a
universal secret detector. Managed fault injection is not a process-kill test at
every instruction. Neither tests nor code review establish a production SLA,
external-directory interoperability, provider-wide revocation or immutable audit
storage. OpenAPI response schemas are incomplete; [API.md](API.md) provides the
companion contract.

The public screenshot and 27-second walkthrough were recorded from the running
application with fictional fixtures. Authentication exchanges, generated private
configuration and browser storage states are excluded from public artifacts.
