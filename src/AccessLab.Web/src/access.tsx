import { useEffect, useState } from "react";
import { api } from "./api";
import type { Access, Resource } from "./types";
import {
  date,
  Empty,
  ErrorNotice,
  Link,
  Loading,
  Modal,
  PageHeading,
  Status,
  useLoad,
} from "./ui";
export function AccessPage({ accountId }: { accountId?: string }) {
  const access = useLoad<Access>(
    "/api/access" +
      (accountId ? "?accountId=" + encodeURIComponent(accountId) : ""),
    10000,
  );
  const [selectedId, setSelectedId] = useState<string>();
  const selected = access.data?.resources.find((r) => r.id === selectedId);
  useEffect(() => {
    if (access.error || (!access.loading && !selected))
      setSelectedId(undefined);
  }, [access.error, access.loading, selected]);
  return (
    <>
      <PageHeading
        title={accountId ? "Account access" : "My access"}

        action={
          <>
            <button className="secondary" onClick={access.refresh}>
              Refresh
            </button>
            <Link className="button" to="/requests/new">
              Request access <span aria-hidden="true">↗</span>
            </Link>
          </>
        }
      />
      {accountId && (
        <p className="context-line">
          Inspecting <strong>{access.data?.accountName || accountId}</strong>.
          Technical roles do not grant resource content.
        </p>
      )}
      {access.loading ? (
        <Loading />
      ) : access.error ? (
        <ErrorNotice error={access.error} onRetry={access.refresh} />
      ) : (
        access.data && (
          <>
            <div className="sync-strip">
              <div>
                <span className="label">Simulated destination</span>
                <Status value={access.data.sync.status} />
              </div>
              <p>
                Desired revision{" "}
                <strong>{access.data.sync.desiredRevision}</strong>
                <span className="divider">/</span>Observed{" "}
                <strong>{access.data.sync.observedRevision}</strong>
              </p>
            </div>
            {access.data.sync.status !== "InSync" && (
              <p className="notice warning">
                The simulated destination is behind the desired state. Local
                revocations still take effect immediately; new grants may not be
                usable yet.
              </p>
            )}
            <section className="panel">
              <div className="section-heading">
                <h2>Resources</h2>
                <span className="meta">
                  Current authorization · dates in UTC
                </span>
              </div>
              {!access.data.resources.length ? (
                <Empty title="No resources in your scope">
                  There are no resources to display for this account.
                </Empty>
              ) : (
                <div className="resource-list">
                  {access.data.resources.map((r) => (
                    <article className="resource-row" key={r.id}>
                      <div className="resource-title">
                        <span className="project">{r.projectId}</span>
                        <h3>{r.name}</h3>
                        <p>{r.description}</p>
                      </div>
                      <div>
                        <Status
                          value={r.effective ? "Available" : "Unavailable"}
                        />
                        <p className="meta">
                          {r.allowed
                            ? r.observed
                              ? "Granted and observed"
                              : "Granted locally · awaiting application"
                            : "No current local permission"}
                        </p>
                        {r.reasons.length > 0 && (
                          <p className="meta">
                            Through{" "}
                            {r.reasons.map((x) => x.groupName).join(", ")}
                          </p>
                        )}
                      </div>
                      <button
                        className="secondary"
                        aria-label={"View " + r.name + " details"}
                        onClick={() => setSelectedId(r.id)}
                      >
                        View details <span aria-hidden="true">→</span>
                      </button>
                    </article>
                  ))}
                </div>
              )}
            </section>
          </>
        )
      )}
      {selected && !access.loading && !access.error && (
        <ResourceDetails
          resource={selected}
          onClose={() => setSelectedId(undefined)}
          inspected={!!accountId}
        />
      )}
    </>
  );
}
function ResourceDetails({
  resource: r,
  onClose,
  inspected,
}: {
  resource: Resource;
  onClose: () => void;
  inspected: boolean;
}) {
  const [result, setResult] = useState<string>();
  const [error, setError] = useState<Error>();
  const [busy, setBusy] = useState(false);
  async function test() {
    setBusy(true);
    setError(undefined);
    setResult(undefined);
    try {
      const content = await api.get<{ message: string }>(
        "/api/resources/" + encodeURIComponent(r.id) + "/content",
      );
      setResult(content.message);
    } catch (e) {
      setError(e as Error);
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal title={r.name} onClose={onClose}>
      <p>{r.description}</p>
      <dl className="facts">
        <div>
          <dt>Project</dt>
          <dd>{r.projectId}</dd>
        </div>
        <div>
          <dt>Eligibility rule</dt>
          <dd>
            Version {r.ruleVersion}
            {r.ruleDescription && <p>{r.ruleDescription}</p>}
          </dd>
        </div>
        <div>
          <dt>Local authorization</dt>
          <dd>{r.allowed ? "Granted" : "Denied"}</dd>
        </div>
        <div>
          <dt>Observed simulator grant</dt>
          <dd>{r.observed ? "Present" : "Absent"}</dd>
        </div>
        <div>
          <dt>Effective access</dt>
          <dd>
            <Status value={r.effective ? "Available" : "Unavailable"} />
          </dd>
        </div>
      </dl>
      {r.denialReason && <p className="notice warning">{r.denialReason}</p>}
      <h3>Access paths</h3>
      {!r.allowed && r.reasons.length > 0 && (
        <p className="hint">
          These membership paths do not override account status or validity
          restrictions.
        </p>
      )}
      {r.reasons.length ? (
        r.reasons.map((reason, i) => (
          <div className="path-row" key={i}>
            <strong>{reason.groupName}</strong>
            <p>
              {date(reason.startsAt)} → {date(reason.endsAt)}
            </p>
            <span className="hint">Start inclusive · end exclusive</span>
          </div>
        ))
      ) : (
        <p className="muted">
          No currently valid group path grants this resource. An active account
          and a valid membership are required.
        </p>
      )}
      <div className="action-footer">
        <p className="hint">
          {inspected
            ? "This test uses your own signed-in identity, not the inspected account."
            : "This makes a real request to the protected resource endpoint."}
        </p>
        <button disabled={busy} onClick={() => void test()}>
          {busy ? "Checking permission…" : "Test resource access"}
        </button>
      </div>
      {error && <ErrorNotice error={error} onRetry={() => void test()} />}{" "}
      {result && (
        <div className="notice success" role="status">
          <strong>Resource responded</strong>
          <p>{result}</p>
        </div>
      )}
    </Modal>
  );
}
