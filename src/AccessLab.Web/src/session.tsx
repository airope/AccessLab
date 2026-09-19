import type { ReactNode } from "react";
import { ApiError } from "./api";
import { ErrorNotice, Loading, useLoad, useMutation } from "./ui";
import type { Session } from "./types";
export function Logout() {
  const mutation = useMutation();
  return (
    <>
      <button
        className="quiet"
        disabled={mutation.busy}
        onClick={async () => {
          const result = await mutation.run<unknown>("/auth/logout", {});
          if (result !== undefined) location.assign("/");
        }}
      >
        {mutation.busy ? "Signing out…" : "Sign out"}
      </button>
      {mutation.error && <ErrorNotice error={mutation.error} />}
    </>
  );
}
export function SessionGate({
  children,
}: {
  children: (session: Session) => ReactNode;
}) {
  const session = useLoad<Session>("/api/session");
  if (session.loading)
    return (
      <div className="auth-shell">
        <Loading />
      </div>
    );
  if (session.error) {
    const status = session.error instanceof ApiError ? session.error.status : 0;
    return (
      <div className="auth-shell">
        <div className="auth-brand">AccessLab</div>
        <main className="auth-main">
          <h1>
            {status === 403
              ? "Access is not configured"
              : status === 401
                ? "Sign in"
                : "Unable to load your workspace"}
          </h1>
          {status === 403 && (
            <p className="muted">
              Your identity is authenticated, but no active AccessLab account is
              available. Contact your local operator. Signing in does not create
              an account or grant permissions.
            </p>
          )}
          {status === 401 ? (
            <>
              <a
                className="button"
                href={
                  "/auth/login?returnUrl=" +
                  encodeURIComponent(
                    location.pathname === "/"
                      ? "/access"
                      : location.pathname + location.search,
                  )
                }
              >
                Continue with Keycloak <span aria-hidden="true">↗</span>
              </a>
              <a
                className="button secondary"
                href={
                  "/auth/login?prompt=login&returnUrl=" +
                  encodeURIComponent(
                    location.pathname === "/"
                      ? "/access"
                      : location.pathname + location.search,
                  )
                }
              >
                Use another account
              </a>
              <p className="hint">
                To change username, choose Restart login beside the current
                username in Keycloak.
              </p>
            </>
          ) : status === 403 ? (
            <>
              <p className="notice warning">{session.error.message}</p>
              <Logout />
            </>
          ) : (
            <ErrorNotice error={session.error} onRetry={session.refresh} />
          )}
          <div className="auth-note">
            <p>
              Demo with fictional identities and a simulated destination. No
              institutional affiliation or real directory integration.
            </p>
          </div>
        </main>
      </div>
    );
  }
  return session.data ? children(session.data) : null;
}
