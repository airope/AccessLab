# HTTP API guide

This guide describes the same-origin BFF interface used by the React application. The local Development runtime also serves `GET /openapi/v1.json`. That generated document discovers routes and request schemas; do not assume every anonymous response has a fully generated OpenAPI schema. Frontend types, endpoint tests and this guide complement it.

## Authentication, errors and mutation protocol

Navigate the browser to `GET /auth/login?returnUrl=/access` for authorization-code OIDC with PKCE. The backend owns the session cookie; browser JavaScript does not store provider tokens. `GET /api/session` resolves the current local account and application roles. A real provider identity without an active local account receives 403, not automatic provisioning.

Before a mutation, obtain an anti-forgery token from `GET /api/csrf`, retain the associated cookie, and send the token in `X-CSRF-TOKEN`. Workflow and lifecycle commands also require `Idempotency-Key`, a UUID generated for that logical operation. Reuse it after an uncertain network outcome with **identical arguments**; generate a new key for a different command. Authorization is rechecked before replay. Never use these headers to impersonate another actor.

Errors use `application/problem+json` with `status`, `title`, `detail`, `code`, and `correlationId`. A caller sees 401 for missing authentication, 403 for forbidden actions, and a non-revealing 404 for scoped objects it may not read. A 409 means the original command may no longer be confirmed: preserve the user's arguments, fetch a fresh preview, and ask for confirmation again. A 503 is a dependency/application failure, not permission to bypass the decision. Do not expose exception stacks or provider protocol values.

`POST /auth/logout` validates anti-forgery and clears the application session. It does not promise provider-wide logout.

Normal login retains the convenience of the provider SSO session. The signed-out screen also offers **Use another account**, calling `GET /auth/login?prompt=login&returnUrl=/access`. This explicitly requests OIDC re-authentication; only the optional value `login` is accepted, and local-return validation is unchanged. Keycloak initially displays the existing username with a **Restart login** action; that native action opens editable username entry. No provider cookies are deleted manually and no roles or identity claims are supplied by the client.

## Preparing and deciding a request

`POST /api/previews` accepts a change command: `accountId`, `groupId`, `operation` (`Add` or `Remove`), UTC ISO `startsAt` and `endsAt`, and a nonblank `reason`. Removal selects an exact `membershipId` where necessary. The server validates affiliation, authority, account eligibility, period, group and overlap. An immediate start is a current UTC timestamp, not a client-side grant.

A preview returns its `id`, `accountRevision`, `expiresAt`, `changes`, `scheduledChanges`, and `warnings`. Its normalized command is stored on the server rather than returned by that response; the accepted decision context later exposes the approved command. Changes are resource-specific Added/Removed/Retained/Unchanged effects with explanations. An empty current diff can coexist with a meaningful future effect. A preview is a bounded calculation, not authorization to mutate forever.

`POST /api/requests` accepts `{previewId}` only. The server uses its stored command; the client cannot quietly replace the beneficiary, group or dates during confirmation. `GET /api/requests` lists only visible requests, with `page`, `pageSize`, `search`, `status` and optional `groupId` filters. `GET /api/requests/{id}` returns a visible request including its actors, period, reason, decision, status and correlation.

A reviewer uses `POST /api/requests/{id}/preview` with an empty JSON object, then `POST /api/requests/{id}/approve` with `{previewId}`. The author and beneficiary cannot approve. `POST /api/requests/{id}/reject` and `POST /api/requests/{id}/cancel` accept `{reason}` and follow distinct reviewer/author permissions. Approved, rejected, cancelled and expired requests cannot be silently changed back to Pending.

## Reading access and membership

`GET /api/access?accountId=...` defaults to the actor's own account. An authorized scoped reader may inspect another account; Operator visibility alone does not grant resource use. The response separates current local grants, observed state, effective access and group-path reasons. `GET /api/resources/{id}/content` requires the acting account's current local right **and** actual resource presence at the simulated destination.

`GET /api/groups` lists the visible fixed group catalog. `GET /api/groups/{id}/members` is a privileged scoped directory. `GET /api/memberships?accountId=...` is subject-scoped and allows a Member to choose their own current or future membership without seeing other group members. Growing collections use `page` and `pageSize`; defaults are 25 and the maximum is 100. The standard envelope is `{items,total,page,pageSize}`; totals must represent the authorized result set.

`GET /api/accounts` returns visible accounts and their read-only validity windows. Only an Operator may `POST /api/accounts/{id}/status` with `{status,reason}`; allowed states are Active, Suspended and Ended. Ended is terminal. A previously issued session does not preserve privileges after suspension or termination.

## Operations and audit

`GET /api/operations` is Operator-only and reports desired versus observed revisions, status, attempts and a safe failure summary. `GET /api/operations/{accountId}/attempts` exposes paginated existing attempt history rather than inventing an external integration log. `POST /api/operations/{accountId}/retry` reconciles current state; it does not directly grant access.

In the explicit local demonstration profile, `POST /api/operations/failure-mode` with `{enabled}` lets an Operator exercise failures. This is not an authentication switch and is disabled outside the demo profile.

`GET /api/audit` offers scoped, paginated correlation/history lookup. An accepted request retains the server preview reference and decision context so later account changes do not rewrite the historical explanation. Audit records expose revision/correlation metadata, never provider tokens or raw idempotency storage.

## Operational boundaries

`GET /health/live` is process health; `GET /health/ready` verifies database reachability. Neither proves an IdP login or successful provisioning; the runbook's dependency check and browser acceptance provide those separate checks.

All timestamps are UTC and membership intervals are half-open: start inclusive, end exclusive. `AccessLab:MaxMembershipDays` defaults to 90 and validates the range 1–3650 at startup; the session exposes the effective value. More than 200 relevant future boundaries or a diagnostic snapshot larger than 64 KiB yields `400 preview_too_complex`, rather than silently omitting effects. Removed membership history does not consume the future-boundary budget. No endpoint edits arbitrary rules, nested groups, provider credentials or administrative role assignments in P0.
