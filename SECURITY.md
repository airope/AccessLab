# Security policy

AccessLab is an experimental access-management application with real OIDC
and a simulated provisioning destination. It is not a production IAM service.
The [security model](docs/SECURITY.md) describes its trust boundaries and limits.

## Reporting a vulnerability

Please use [GitHub private vulnerability reporting](https://github.com/airope/AccessLab/security/advisories/new).
Do not open a public issue containing a working exploit, credentials or private
data. Include the affected commit, a minimal reproduction using fictional
fixtures, expected behavior and the impact you observed.

Never attach `.local/`, connection strings, cookies, tokens, browser storage
states or unredacted authentication traces. A correlation ID and sanitized
request shape are usually enough to begin investigation.

Security fixes target the current `main` branch. Older revisions have no
maintenance guarantee. This is a community-maintained project without a
response-time SLA; do not deploy it where an unreviewed failure could affect
real users or resources.
