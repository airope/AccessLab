# Requirements and verification map

This maps the [functional requirements and acceptance criteria](SPECIFICATION.md) to implementation and verification anchors. **Execution results, the tested version and limitations are recorded in [VALIDATION.md](VALIDATION.md).** A test name in this table describes intended coverage, not an execution result or a claim that every possible case has been tested. External provisioning integrations, nested groups and a policy editor are outside scope.

Cross-cutting requirements include retained decision context, scoped pagination, account validity windows, configurable duration limits and explicit refusal of previews exceeding the complexity budget. The execution gate is recorded separately rather than inferred from the presence of source files.

## Functional requirements

| Requirement | Implementation | Verification anchors |
|---|---|---|
| F-01 Real OIDC, local logout | `Program.cs`, `Data.cs:ActorService`, `session.tsx` | Real-Keycloak Chrome login/unknown identity/logout; `IdentityTests`, `DatabaseIdentityTests` |
| F-02 Effective access and reasons | `Domain.cs:AccessService`; resource-specific actual-destination gate; `access.tsx` | `Removal_retains_library_through_knowledge`; `ReviewRegressionTests`; real protected-content checks |
| F-03 Bounded project/period request | `Workflow.Validate`, `AuthorizeCommand`; `requests.tsx`; PostgreSQL overlap constraint | `WorkflowTests`, `DatabaseInvariantTests`, `InputValidationTests`, configured-duration cases in `CompletionTests` |
| F-04 Fresh submission/review previews | `Workflow.Preview`, `Fingerprint`, `Fresh`; `ImpactPreview`, `PreviewConfirmation` | `AcceptanceRulesTests`, `ReliabilityTests`; explicit UI conflict refresh and expiry; scheduled UI scenario |
| F-05 Independent decision | `Workflow.Reviewer`, `Approve`; author and beneficiary exclusion | `Author_and_beneficiary_cannot_review_on_behalf_request`; direct-approval/no-side-effect regression; real reviewer journey |
| F-06 Scoped groups/members | `Scope`, read endpoints, subject-scoped memberships; `directory.tsx` | Membership privacy, same-project unrelated member, group-filter/count and bounded-page tests |
| F-07 Account lifecycle | `Account.Active`, `ActorService`, lifecycle endpoint and `AccountLifecycle` | Suspension with existing cookie, Ended terminality and old-session denial, resume-after-crash regression |
| F-08 Time independent of worker | Current clock evaluated by `AccessService.Desired` and account validity | `Expiration_denies_content_with_worker_stopped`; controlled-clock preview cases |
| F-09 Durable simulated application | `Workflow.Project`, `Reconciler`, `SimulatedDestination`, outbox/lease/projections | Five failures/retry, crash-boundary recovery, monotone revision, pending/failed drift and actual-presence regressions |
| F-10 Support diagnostics | Correlated audit, accepted preview/context snapshot, paginated attempt history; request/audit/operations screens | Context survives dependency changes; stored context protection; scoped attempt reads; browser diagnostic assertions |
| F-11 Safe errors/conflicts | `ProblemMiddleware`, `ApiError`, `ErrorNotice`, stable-key `useMutation` | Invalid body, anti-forgery, safe redirect, 409 refresh, network/503 UI state and retry tests |
| F-12 Reproducible execution/demo | Native/Compose scripts, EF migrations, CI definition, README/runbook/API/demo guides | `scripts/verify.py --browser`, setup test, application DB reset/migration rehearsal; explicit container/hosted-CI limits in validation |

Backend paths are relative to `src/AccessLab.Api/`; frontend paths to `src/AccessLab.Web/src/`; C# tests to `tests/AccessLab.Api.Tests/`. Browser tests are in `tests/e2e/specs/`.

## Acceptance criteria

| ID | Specific test or observed path | Scope of evidence |
|---|---|---|
| A-01 | Real OIDC fixture IDs; real unknown identity denied; `DatabaseIdentityTests` | Real local provider plus separate HTTP identity tests; not a certification of all provider protocol errors |
| A-02 | `Request_group_filter_and_scoped_counts_do_not_reveal_unrelated_member` | Same-project unrelated Member object/list isolation, not merely a cross-project reviewer check |
| A-03 | `Author_and_beneficiary_cannot_review_on_behalf_request`; Eve's request read/approval refusal | Direct scoped HTTP checks |
| A-04 | `Direct_author_and_beneficiary_approval_has_no_side_effects` | Direct approval by excluded actors, including no decision/membership/outbox/audit side effects |
| A-05 | Lifecycle stale-preview and post-lock authority recheck tests | Controlled dependency changes and database locking; no exhaustive scheduler interleaving claim |
| A-06 | Five-minute age, future-start and time-boundary tests | Controlled clock and explicit browser refresh; future impact is not hidden when current diff is empty |
| A-07 | `Two_simultaneous_approvals_have_one_logical_winner` | One decision, membership, audit and outbox result from competing commands |
| A-08 | Workflow replay/mismatch, response-loss, revoked-authority and cross-actor-key tests | Actor-scoped replay with fresh permission checks |
| A-09 | `Removal_retains_library_through_knowledge`; Chrome Results denial/Library success | Multiple membership paths, not a flat grant flag |
| A-10 | `Expiration_denies_content_with_worker_stopped` | Exact exclusive end boundary without a running worker |
| A-11 | Suspension/browser cookie and `Ended_account_cannot_use_existing_session` | Current application authority overrides old session claims |
| A-12 | Five durable failures, retry and worker crash recovery; real demo failure switch | Simulated destination only; no external directory claim |
| A-13 | `Simulator_cannot_overwrite_new_revision_with_old_grant` | Monotone destination revisions reject stale reactivation |
| A-14 | Transaction rollback and lease/destination/acknowledgement fault tests | Named managed-exception boundaries, not all OS/network interruption points |
| A-15 | Missing/invalid CSRF, logout CSRF and encoded nonlocal return tests | Mutation protection and local return validation |
| A-16 | Framework logging sentinel, inert component text and stored-text browser scenario | Targeted logging/XSS/private-material checks; not a complete penetration test |
| A-17 | Desktop/mobile six-screen scans, real form/review/cancel, keyboard-focused security UI suite | Exercised states and keyboard paths only; Axe is not full accessibility certification |
| A-18 | Setup idempotence, real PostgreSQL migrations/tests, native application reset and browser verification | Local provisioned environment and clean application schema; not blank-machine, Docker or hosted-CI certification |

## Additional regression boundaries

1. **Actual destination presence:** the resource endpoint and effective-access flag no longer trust a stale acknowledgement cache after destination revocation followed by a crash and account resumption. `Resume_after_unacknowledged_revocation_requires_destination_presence` exercises that boundary. Unchanged valid rights remain usable while unrelated changes are pending.
2. **Drift while Pending/Failed:** an observed revision ahead of desired state causes a new immutable projection above that revision; retry no longer loops on an obsolete projection. `Ahead_destination_converges_from_pending_or_failed` and `Unchanged_desired_projection_respects_a_higher_observed_floor` cover this case.
3. **Malformed requests and redirects:** missing command fields fail with a safe 400 before object lookup, and encoded nonlocal returns are rejected before OIDC challenge.
4. **Narrow layout:** visually hidden content is contained without clipping ordinary horizontal table scrolling. The page itself no longer overflows the viewport in the exercised mobile screens.
5. **Read consistency and scoped affiliations:** `ReadSnapshotTests` interleaves real worker delivery with revocation/suspension; `AccountScopeTests` checks multi-project account visibility. Access content and diagnostics share a read-only snapshot beginning before actor resolution.
6. **UI state correctness:** component regressions preserve beneficiary identity across pagination, serialize polling, close stale resource dialogs and recover terminal request conflicts by reloading the request.
7. **Delivery and test safety:** `scripts/test_delivery.py`, `scripts/test_e2e.py` and `tests/e2e/cleanup.test.mjs` cover generated-file replacement, candidate filename policy, locked-restore configuration, served-asset identity, isolated runner cleanup and exact run-owned membership cleanup. Native browser acceptance now uses a fresh temporary application database, not the user's current demonstration state.

See the [security model](SECURITY.md) for bounded guarantees and [validation record](VALIDATION.md) for the distinction between inspected configuration and executed behavior.

## Honest boundaries

The simulator, conservative single-organization database locking, fixed rules, fixture-provisioned privileged roles and append-only application audit are intentional P0 choices. This delivery does not include SCIM, arbitrary policies, provider-wide token revocation, multi-tenancy, notification services, production operational guarantees or a real external provisioning connector. Operational limits and remaining verification boundaries are documented, not replaced by fabricated success results.
