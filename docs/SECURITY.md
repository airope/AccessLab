# Security model and verification boundaries

AccessLab is a local access-management laboratory, not a certified identity-management product. Its protected object is an access decision: who may change which membership, for whom, and under which current authority. The identity provider authenticates a person; it does not decide the application's business permissions.

This document describes the security contract. Execution evidence and remaining verification limits belong in [VALIDATION.md](VALIDATION.md); the presence of a requirement here is not evidence that a test passed. Requirement IDs below refer to the [specification](SPECIFICATION.md), with implementation and test anchors in [coverage](COVERAGE.md).

## Trust boundaries

The browser is untrusted. Account identifiers, group identifiers, timestamps, displayed role names and preview results received from a client are not authority. The backend resolves the actor from its OIDC-backed session and the local account identified by issuer and subject. It rechecks the account and scoped application roles against the database. An email or preferred username is not an account-linking key.

PostgreSQL is the authority for the workflow, actor permissions, memberships and desired state. An application transaction commits a decision together with its audit record and durable application event. Database administrators are outside the append-only audit guarantee: this application does not offer cryptographic or external immutable audit storage.

The destination connector is deliberately a simulator. Its actual grants and last acknowledged state are stored separately from desired grants. It demonstrates failure recovery and monotone revision application; it does not prove compatibility or failure behavior of an external directory. Access to the demonstration resource requires both current local authorization and the simulator's actual grant, not merely a cached acknowledgement. An unrelated pending change must not revoke an otherwise valid unchanged grant.

Access diagnostics and protected content use a read-only PostgreSQL `RepeatableRead` snapshot established before actor resolution. A concurrent change cannot cause those reads to combine old authority with new destination state. Snapshot consistency is not instantaneous cancellation of an already authorized response: later requests recheck the updated state. No cached observed grant can substitute for actual destination presence.

## Threats and required defenses

| Threat | Defense | Verification target |
|---|---|---|
| Reading another member's request by changing a URL | Object-level checks, non-revealing not-found response, scoped lists and counts | A-02, A-03 |
| Acquiring a platform role through a business group | Separate local RoleGrant and Group models; no role assignment from request input | A-01, F-06 |
| Self-approving a request | Both submitting actor and beneficiary excluded from approval | A-04 |
| Approving a stale or future-boundary preview | Server-stored normalized arguments, account aggregate revision, rule/authority checks and bounded validity | A-05, A-06 |
| Racing approvals or suspension against a decision | Transactional locks and constraints, one terminal transition, audit/outbox committed atomically | A-07, A-14 |
| Replaying a key for different arguments or a different actor | Idempotency scoped to actor and operation, payload comparison, fresh authorization before replay | A-08 |
| Retaining access with an old session after suspension | Current account and permissions checked in the backend, not merely cookie claims | A-11 |
| Retaining a timed right because a worker is stopped | Local authorization evaluates the current time on every access decision | A-10 |
| An old application message restoring a revoked right | Immutable projections, monotone destination revisions, current-state reconciliation | A-12, A-13 |
| Cross-site mutation through a session cookie | Anti-forgery tokens on mutations including logout; exact redirect URLs and local return paths | A-15 |
| Script execution in a justification | Text rendering, bounded input, content-security policy; no trusted user HTML | A-16 |
| Leakage through diagnostics or committed artifacts | Structured non-sensitive errors, no token/password logging, private generated runtime directory ignored by Git | A-16 |

## Local profile and unsupported deployment scope

The local profile uses disposable users and generated private credentials, a loopback identity-provider HTTP endpoint, and a locally generated HTTPS certificate for the application. Ignoring the certificate in a local browser test is not a production TLS policy. Never reuse these users, secrets, certificates or databases in an internet-facing service.

The fault simulation control is only for the explicit local demo profile. It is not an identity switch and must not create an authentication bypass. HTTP integration tests may replace authentication through their test host's dependency injection; that handler must remain in the test assembly, not be selectable by a runtime setting or HTTP header in the application.

No production deployment or hardening guarantee is provided. The local profile uses a privileged database role and unencrypted-at-rest application data-protection keys; host permissions are not managed key storage. The first version does not implement enterprise offboarding of provider identities, provider-wide token revocation, SCIM, MFA policy administration, nested groups or multi-tenant isolation. Signing out locally is not a promise to revoke all provider sessions on other applications.

## Development and disclosure

Do not attach `.local/`, connection strings, cookie jars, identity-provider imports, browser storage states, or unredacted HTTP traces to a public issue. The default acceptance suite disables traces because authentication exchanges can contain sensitive protocol values. Use the correlation identifier and a minimal description of the authorized reproduction instead.

Dependency scans, automated accessibility checks, negative tests and independent code review provide useful evidence but are not a penetration-test certification. Publish only the actual test scope and observed result, together with the residual limits.
