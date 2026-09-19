# Demonstrating AccessLab

AccessLab is a local access-management laboratory with fictional identities and projects. Authentication uses real Keycloak OIDC; provisioning targets an explicitly simulated service. This walkthrough exercises a temporary membership from request through independent approval and application.

## Preparation

Start the three services using the [runbook](RUNBOOK.md) and open `https://localhost:5443`. Use Alice as the member, Bob as the reviewer and Claire as the operator. Separate browser profiles or private contexts allow simultaneous sessions. Local sign-out clears AccessLab's session; Keycloak may retain its own SSO session. Generated disposable passwords are in the private `.local/credentials.json`; never include that file or the sign-in exchange in a recording.

To change accounts in the same browser, choose **Sign out → Use another account**. On Keycloak, choose **Restart login** beside the current username, then enter the other account. **Continue with Keycloak** deliberately retains automatic SSO. Separate contexts are only needed when demonstrating several roles simultaneously.

For a repeatable initial state, follow the explicit reset procedure in the runbook while the API is stopped. It destroys only this application's fictional decision history. Do not reset a database with information you need to keep.

## Try the workflow

**Inspect existing access.** As Alice, open My access. In the initial fixtures, Knowledge provides Shared library, while Results dashboard is unavailable. Open resource details to inspect the group path, dates and eligibility rule.

**Prepare one bounded change.** Request Analysis membership until tomorrow, enter a fictional reason, then preview. Results is added; Library is retained through another path. The server stores the preview and normalized arguments. Submit the request: this records a request, not an active right. If the preview expires or dependencies change, refresh and confirm the new effects rather than resubmitting the old confirmation.

**Use a different decision maker.** In Bob's context, open the pending request. Refresh its impact before approving. Alice cannot approve her own request, and a reviewer cannot approve a request they authored for someone else. The server enforces those rules independently of whether a button is visible.

**Separate approval from application.** Return to Alice and refresh My access. Show desired versus observed state and use the protected resource action. The content endpoint requires both current local authorization and the simulator's actual grant; a pending unrelated change does not disable an unchanged valid resource.

**Explain a removal.** Request removal of the exact Analysis membership. Its preview removes Results, but retains Library through Knowledge. Have Bob approve it. This illustrates why group membership is not equivalent to a single flat permission flag.

**Make failure visible.** As Claire, enable local connector-failure simulation. Have Alice and Bob complete another independently reviewed change. Operations shows the unsuccessful application rather than a false success. Disable failure and retry: the system reconciles current desired state instead of blindly replaying old grants. Inspect attempt history and the correlated audit record.

**Finish with lifecycle.** Suspend Alice as Claire with a reason. Alice's existing session immediately loses business access, even if a destination operation is still pending. Resume if continuing the demo; Ended is deliberately terminal. Expiration is also evaluated in the request path and does not depend on a worker running on time.

## Explore the implementation

- `Program.cs` and `Data.cs`: how does an OIDC issuer/subject map to a local account, and why are provider roles not application authority?
- `Workflow.cs`: what makes a preview stale; what is locked; what does idempotency mean after an actor loses permission?
- `Provisioning.cs`: why use immutable projections, a durable outbox and monotone revisions rather than a fire-and-forget HTTP call?
- `Domain.cs`: why distinguish desired, last-observed and actually effective access?
- `tests/AccessLab.Api.Tests`: which exact clock, concurrent-decision and crash boundaries are tested, and which are not?

## Honest limits

This is a small single-organization modular monolith. The conservative database locking strategy is not a throughput benchmark. Fault tests inject controlled exceptions; they are not certification of every OS/network failure. Audit is append-only through the application, not immutable against a database administrator. The simulator demonstrates a recovery protocol, not interoperability with a production directory. Consult [validation](VALIDATION.md) for execution scope, and the [specification](SPECIFICATION.md) for acceptance criteria. A workflow file alone is not evidence of a hosted CI pass.
