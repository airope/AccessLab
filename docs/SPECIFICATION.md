# AccessLab specification

## Purpose and scope

AccessLab models a temporary access decision from authenticated request through independent review, asynchronous application and eventual removal. It is a local, single-organization laboratory with two isolated fictional projects, non-nested groups and fixed, versioned resource rules. Keycloak provides real OIDC authentication. The provisioning destination is an in-process simulator backed by PostgreSQL, not an external directory.

This document defines required behavior, not proof of execution. [COVERAGE.md](COVERAGE.md) maps requirements to code and tests; [VALIDATION.md](VALIDATION.md) records execution scope and remaining verification limits. See [API.md](API.md) for the HTTP contract and [RUNBOOK.md](RUNBOOK.md) for local operation.

## Domain and authority

- **Account:** local identity linked by exact OIDC issuer and immutable subject, with project affiliations, validity window, status and revision. Email and provider role claims do not confer application authority.
- **Application roles:** Member requests their own access; scoped Reviewer handles authorized requests and may act on behalf of a beneficiary; Auditor has scoped diagnostic visibility; Operator manages account lifecycle and connector operations. Roles are installation data, separate from requestable business groups. Operator status alone does not grant resource content.
- **Membership:** a dated relationship between account and group. A resource can have several independent membership paths. Removing one path need not remove access through another.
- **Request:** author, beneficiary, exact change, reason and decision history. Pending requests can become Approved, Rejected, Cancelled or Expired; terminal states do not silently return to Pending.
- **Preview:** server-stored normalized command, dependency fingerprint, effects, scheduled effects and expiry. Requester and reviewer previews are distinct. A preview identifier does not confer permanent permission.
- **Projection:** an immutable complete desired resource set at a monotone revision. A durable outbox schedules application. Last-observed acknowledgement and actual simulated destination state are distinct.

## Decision invariants

1. Every protected operation resolves current local authority. Unmapped or inactive accounts receive no business permissions. Lists, totals and object reads respect scope; the UI is not an authorization boundary.
2. Membership periods are UTC, start-inclusive and end-exclusive. Server validation enforces account/project eligibility, valid ordered dates and non-overlap. `AccessLab:MaxMembershipDays` defaults to **90**, accepts **1–3650** at startup and is exposed by the session for client guidance.
3. Submit accepts the stored preview ID, not a replacement client-authored command. Review requires a fresh reviewer preview. Validity is at most five minutes and is shortened by the next relevant time boundary. Changed dependencies require recalculation and confirmation.
4. More than **200 relevant future boundaries**, or a decision-context snapshot over **64 KiB**, produces `400 preview_too_complex`; effects must not be silently omitted. Removed membership history does not consume the boundary budget.
5. Neither the author nor the beneficiary may approve their request, even with combined roles. Approval rechecks permission, scope, state, revisions and time under database locks. Decision, membership, audit and outbox commit atomically.
6. Idempotency keys are actor/operation scoped. Identical replay returns the existing result only after fresh authorization; different arguments conflict. Concurrent approvals produce one logical result. Accepted decision context is retained rather than recomputed from later dependencies.
7. Effective resource access requires current local eligibility **and actual presence of that resource at the simulated destination**, not a cached observed grant. Unrelated pending changes must not deny an unchanged valid resource.
8. Access diagnostics and content use a read-only PostgreSQL `RepeatableRead` transaction opened before actor resolution. Each response uses a consistent snapshot of authority, membership and destination state. This does not cancel an already authorized response whose snapshot precedes a concurrent revocation; subsequent requests re-evaluate authority.
9. Suspension, termination and time expiry deny subsequent local access even with an old session or stopped worker. Suspended accounts can resume; Ended is terminal. Local denial and destination convergence are separate outcomes.
10. Five consecutive connector failures lead to Failed, not false success. Authorized retry reconciles current state. Old projections cannot overwrite newer destination revisions or reactivate stale grants.

## Functional requirements

| ID | Required behavior |
|---|---|
| F-01 | Authenticate through real OIDC authorization-code flow with PKCE, map issuer/subject to a local account, and support CSRF-protected local logout without claiming global IdP revocation. |
| F-02 | Show local eligibility, observed synchronization and effective access separately, with resource rules, denial reasons, group paths and validity dates. Protect actual resource content server-side. |
| F-03 | Create project-scoped, time-bounded Add or exact-membership Remove requests with a nonblank reason and server validation of target, authority, dates and overlap. |
| F-04 | Require server-generated impact previews before submission and approval; show Added/Removed/Retained/Unchanged effects, scheduled changes, warnings and expiry. Refuse stale or over-complex previews explicitly. |
| F-05 | Enforce independent review, scoped approval/rejection and author cancellation. Preserve terminal states, immutable accepted context and atomic decision effects. |
| F-06 | Expose only authorized groups, accounts, memberships and requests. Separate self-service membership selection from privileged group-member browsing; paginate growing collections with authorized totals. |
| F-07 | Let an Operator suspend, resume or end an application account with a reason and audit record. Recheck current status and validity rather than trusting old session claims. |
| F-08 | Evaluate membership/account time bounds in the access path independently of background processing; future grants do not become active early. |
| F-09 | Persist desired projections and outbox work, apply them through the simulator, expose Pending/Failed/InSync outcomes and support safe retry, drift repair and crash recovery. |
| F-10 | Provide scoped request, audit and operation diagnostics with correlation IDs, stored decision explanations, revision metadata and paginated attempt history. |
| F-11 | Return safe structured errors and usable UI refusal/conflict states for 401, 403, non-revealing 404, 409 and 503. Preserve input for deliberate refresh/reconfirmation; do not bypass security on dependency failure. |
| F-12 | Supply reproducible local setup, migrations, explicit disposable-data reset, build/test automation and a documented walkthrough. Distinguish available scripts from environments actually verified. |

## Acceptance criteria

| ID | Observable acceptance condition |
|---|---|
| A-01 | Real OIDC login maps to the expected local account. Unknown issuer/subject grants no privileges; provider roles and email cannot replace local mapping. |
| A-02 | Changing a request identifier cannot reveal another member's request. Direct HTTP reads, filtered lists and counts enforce the same scope. |
| A-03 | A reviewer for one project cannot read or approve requests in the other project. |
| A-04 | Direct approval by author or beneficiary is refused without decision, membership, outbox or audit side effects from that approval. |
| A-05 | A relevant dependency change invalidates the preview; confirmation returns 409 without applying the change, and a fresh preview can be requested. |
| A-06 | An aged preview or one crossing a relevant time boundary is refused. Future effects remain visible even if the current effect set is empty. |
| A-07 | Two simultaneous approvals yield one terminal decision, one logical membership change and coherent audit/outbox state. |
| A-08 | Identical authorized replay returns the same result; changed arguments conflict. Another actor cannot retrieve the first actor's replay result, and revoked authority cannot reuse a successful key to bypass checks. |
| A-09 | Removing Analysis preserves Shared library through Knowledge while denying Results when no other valid path remains; the surviving path is explained. |
| A-10 | At the exclusive membership end boundary, protected content is denied even when the worker is stopped, using a controlled clock test. |
| A-11 | Suspension and Ended status deny business access through an existing session. Resumption after unacknowledged destination revocation cannot rely on a stale observed cache. |
| A-12 | Connector failure never produces false InSync confirmation; retry converges from current desired state without duplicate logical grants. |
| A-13 | An older projection delivered after a newer revocation cannot overwrite the newer destination revision or reactivate the grant. |
| A-14 | Controlled faults at transaction, response-loss, worker lease, destination and acknowledgement boundaries preserve atomicity or allow durable recovery. This criterion is bounded to named test hooks, not every possible OS interruption. |
| A-15 | Missing/invalid anti-CSRF tokens cause mutation refusal, including logout. External or encoded nonlocal login return URLs are rejected. |
| A-16 | Submitted text renders inert; targeted logging and public-material checks detect prohibited secret/token output. This is not an exhaustive penetration-test claim. |
| A-17 | Essential request, preview, review and cancellation paths work at narrow widths and by keyboard, with intelligible errors and conflicts. Automated accessibility scans supplement, not replace, manual checks. |
| A-18 | The documented local setup can reach an applied request with reproducible migrations, explicit fixture reset and executable tests. Clean-install, native, Compose and hosted-CI evidence must be reported separately; unexecuted environments remain unverified. |

## Non-goals and limits

No SCIM or production directory connector, arbitrary policy editing, nested groups, multi-tenancy, notifications, provider-wide revocation, production hosting guide or availability SLA is included. Audit is append-only through the application, not immutable against a database administrator. Conservative locking is not a throughput benchmark. See the [architecture](ARCHITECTURE.md) and [security model](SECURITY.md) for boundaries; use the [demo](DEMO.md) to try the workflow.
