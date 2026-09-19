# Contributing

Bug reports, focused fixes and documentation improvements are welcome. For a
new integration or a substantial feature, open a design issue first so the
scope and security boundary can be discussed before implementation.

## Development

Use the versions pinned in `global.json`, the manifests and lockfiles. Follow
[the runbook](docs/RUNBOOK.md) for local PostgreSQL, Keycloak and HTTPS setup.
All fixture credentials are generated into ignored `.local/` files. Never use
real organizational identities or data in tests, examples or screenshots.

Useful checks from the repository root:

```sh
dotnet restore --locked-mode
dotnet tool restore
dotnet tool run csharpier check src/AccessLab.Api tests/AccessLab.Api.Tests
python3 -m unittest discover -s scripts -p 'test_*.py'
npm --prefix src/AccessLab.Web ci
npm --prefix src/AccessLab.Web test
npm --prefix src/AccessLab.Web run format:check
npm --prefix src/AccessLab.Web run build
npm --prefix tests/e2e ci
python3 scripts/check-secrets.py
```

For the complete native suite, leave PostgreSQL and Keycloak running, stop the
normal API and run `python3 scripts/verify.py --browser`. The runner creates a
fresh browser-test database; restart `python3 scripts/native.py api` afterwards.
Do not bypass the isolation guard against a database whose contents matter.
Backend integration tests recreate their dedicated test schema: never run
multiple suites against the same test database concurrently.

## Pull requests

Keep changes focused. Explain the behavior being changed, the failure mode and
how it was verified. For a bug fix, include a regression that fails before the
fix where practical. Preserve existing assertions; do not remove authorization,
CSRF, concurrency or real-OIDC checks to make a build pass.

Authoritative permissions belong on the server. Client-side visibility is not an
access-control boundary. Keep request approval distinct from destination
application, retain explicit conflict recovery and document new failure modes.
Schema changes need an EF migration; dependency changes need updated lockfiles.

AI-assisted contributions are welcome, but the contributor remains responsible
for understanding the change, its provenance, license compatibility and tests.
Do not submit generated claims of successful execution without running the checks.

Be respectful and specific in reviews. Report suspected vulnerabilities through
[the private reporting route](SECURITY.md), not a public issue. Contributions are
accepted under the repository's [MIT license](LICENSE); third-party material
must retain its own notices and attribution.
