import { useState, type FormEvent } from "react";
import { query } from "./api";
import type {
  AuditEvent,
  Operation,
  Page,
  Session,
  SyncAttempt,
} from "./types";
import {
  date,
  Empty,
  ErrorNotice,
  Link,
  Loading,
  Modal,
  PageHeading,
  Pagination,
  ReasonField,
  Status,
  useLoad,
  useMutation,
} from "./ui";
export function OperationsPage({ session }: { session: Session }) {
  const [page, setPage] = useState(1);
  const [history, setHistory] = useState<Operation>();
  const operations = useLoad<Page<Operation> & { failureMode: boolean }>(
    "/api/operations?" + query({ page, pageSize: 25 }),
    5000,
  );
  const [selected, setSelected] = useState<Operation>();
  const [filter, setFilter] = useState("");
  const [message, setMessage] = useState("");
  const mutation = useMutation();
  async function toggle() {
    const result = await mutation.run<{ enabled: boolean }>(
      "/api/operations/failure-mode",
      { enabled: !operations.data?.failureMode },
    );
    if (result) {
      setMessage(
        result.enabled
          ? "Failure simulation enabled. New attempts will fail."
          : "Failure simulation disabled. Retry failed accounts to reconcile.",
      );
      operations.refresh();
    }
  }
  return (
    <>
      <PageHeading
        title="Operations"

        action={
          <button className="secondary" onClick={operations.refresh}>
            Refresh
          </button>
        }
      />
      <p className="notice neutral">
        <strong>Simulated destination</strong> — this connector does not
        provision a real directory or laboratory system. Retrying reconciles the
        current desired state; it never creates a new business permission.
      </p>
      {message && (
        <p role="status" className="notice success">
          {message}
        </p>
      )}
      {mutation.error && <ErrorNotice error={mutation.error} />}{" "}
      {session.demoMode && operations.data && (
        <section
          className={
            "demo-controls " +
            (operations.data.failureMode ? "failure-active" : "")
          }
        >
          <div>
            <h2>
              {operations.data.failureMode
                ? "Failure simulation is on"
                : "Connector failure simulation"}
            </h2>
            <p>
              {operations.data.failureMode
                ? "The simulator is deliberately failing. After five attempts, synchronization is marked Failed."
                : "Use this audited control to exercise the failure → retry → reconciliation path."}
            </p>
          </div>
          <button
            className={
              operations.data.failureMode ? "secondary" : "danger-secondary"
            }
            disabled={mutation.busy}
            onClick={() => void toggle()}
          >
            {mutation.busy
              ? "Updating…"
              : operations.data.failureMode
                ? "Disable failure simulation"
                : "Enable failure simulation"}
          </button>
        </section>
      )}
      <section className="panel">
        <div className="toolbar">
          <label className="field">
            Synchronization status on this page
            <select value={filter} onChange={(e) => setFilter(e.target.value)}>
              <option value="">All states</option>
              {["Pending", "Failed", "InSync"].map((s) => (
                <option key={s} value={s}>
                  {s === "InSync" ? "In sync" : s}
                </option>
              ))}
            </select>
          </label>
          <span className="hint">Refreshes every 5 seconds while visible</span>
        </div>
        {operations.loading ? (
          <Loading />
        ) : operations.error ? (
          <ErrorNotice error={operations.error} onRetry={operations.refresh} />
        ) : (
          operations.data && (
            <>
              {operations.data.items.filter(
                (o) => !filter || o.status === filter,
              ).length ? (
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Account</th>
                        <th>Synchronization</th>
                        <th>Desired / observed</th>
                        <th>Attempts</th>
                        <th>Last outcome</th>
                        <th>Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {operations.data.items
                        .filter((o) => !filter || o.status === filter)
                        .map((o) => (
                          <tr key={o.accountId}>
                            <th scope="row">
                              <Link
                                to={
                                  "/access?accountId=" +
                                  encodeURIComponent(o.accountId)
                                }
                              >
                                {o.accountName}
                              </Link>
                            </th>
                            <td>
                              <Status value={o.status} />
                            </td>
                            <td className="numeric">
                              {o.desiredRevision} / {o.observedRevision}
                            </td>
                            <td className="numeric">{o.attempts}</td>
                            <td>
                              {o.lastError || "No reported error"}
                              <span className="cell-sub">
                                {date(o.updatedAt)}
                              </span>
                            </td>
                            <td>
                              <button
                                className="secondary"
                                aria-label={
                                  "Attempt history for " + o.accountName
                                }
                                onClick={() => setHistory(o)}
                              >
                                Attempt history
                              </button>{" "}
                              <button
                                className="secondary"
                                aria-label={"Retry " + o.accountName}
                                onClick={() => setSelected(o)}
                              >
                                Retry
                              </button>
                            </td>
                          </tr>
                        ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <Empty title="No matching synchronization states">
                  Choose another state, browse another page, or refresh the
                  queue.
                </Empty>
              )}
              {operations.data.total !== undefined && (
                <Pagination
                  page={page}
                  total={operations.data.total}
                  pageSize={operations.data.pageSize}
                  onChange={setPage}
                />
              )}
            </>
          )
        )}
      </section>
      {history && (
        <AttemptHistory
          operation={history}
          onClose={() => setHistory(undefined)}
        />
      )}
      {selected && (
        <Retry
          operation={selected}
          onClose={() => setSelected(undefined)}
          onSaved={() => {
            setSelected(undefined);
            setMessage(
              "Reconciliation queued. This is not yet proof of application.",
            );
            operations.refresh();
          }}
        />
      )}
    </>
  );
}
function AttemptHistory({
  operation,
  onClose,
}: {
  operation: Operation;
  onClose: () => void;
}) {
  const [page, setPage] = useState(1);
  const attempts = useLoad<Page<SyncAttempt>>(
    "/api/operations/" +
      encodeURIComponent(operation.accountId) +
      "/attempts?" +
      query({ page, pageSize: 25 }),
  );
  return (
    <Modal
      title={"Attempt history for " + operation.accountName}
      onClose={onClose}
    >
      <p className="hint">
        Persisted simulator attempts. Projection revision identifies the state
        sent, not the account revision used for approval.
      </p>
      {attempts.loading ? (
        <Loading />
      ) : attempts.error ? (
        <ErrorNotice error={attempts.error} onRetry={attempts.refresh} />
      ) : (
        attempts.data && (
          <>
            {attempts.data.items.length ? (
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>Attempt / time (UTC)</th>
                      <th>Projection Revision</th>
                      <th>Outcome</th>
                      <th>Error</th>
                    </tr>
                  </thead>
                  <tbody>
                    {attempts.data.items.map((a) => (
                      <tr key={a.id}>
                        <th scope="row">
                          <code>{a.id}</code>
                          <span className="cell-sub">
                            {date(a.attemptedAt)}
                          </span>
                        </th>
                        <td className="numeric">{a.revision}</td>
                        <td>
                          <Status value={a.outcome} />
                        </td>
                        <td>{a.error ?? "No reported error"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <Empty title="No recorded attempts">
                No simulator attempts are recorded for this account.
              </Empty>
            )}
            <Pagination
              page={page}
              total={attempts.data.total}
              pageSize={attempts.data.pageSize}
              onChange={setPage}
            />
          </>
        )
      )}
    </Modal>
  );
}
function Retry({
  operation,
  onClose,
  onSaved,
}: {
  operation: Operation;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [reason, setReason] = useState("");
  const mutation = useMutation();
  async function submit(e: FormEvent) {
    e.preventDefault();
    const result = await mutation.run<{ accepted: boolean }>(
      "/api/operations/" + encodeURIComponent(operation.accountId) + "/retry",
      { reason },
    );
    if (result?.accepted) onSaved();
  }
  return (
    <Modal
      title={"Reconcile " + operation.accountName}
      onClose={() => {
        if (!mutation.busy) onClose();
      }}
    >
      <p>
        The worker will reevaluate current permissions, account status, and time
        boundaries. Old grants will not be replayed.
      </p>
      <dl className="facts">
        <div>
          <dt>Desired revision</dt>
          <dd>{operation.desiredRevision}</dd>
        </div>
        <div>
          <dt>Observed revision</dt>
          <dd>{operation.observedRevision}</dd>
        </div>
      </dl>
      {operation.lastError && (
        <p className="notice warning">{operation.lastError}</p>
      )}
      <form onSubmit={submit}>
        <fieldset disabled={mutation.busy}>
          <ReasonField value={reason} onChange={setReason} />
        </fieldset>
        {mutation.error && <ErrorNotice error={mutation.error} />}
        <button disabled={mutation.busy}>
          {mutation.busy ? "Queueing…" : "Retry reconciliation"}
        </button>
      </form>
    </Modal>
  );
}
export function AuditPage() {
  const initialSearch =
    new URLSearchParams(location.search).get("search") || "";
  const [search, setSearch] = useState(initialSearch);
  const [draft, setDraft] = useState(initialSearch);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [selected, setSelected] = useState<AuditEvent>();
  const audit = useLoad<Page<AuditEvent>>(
    "/api/audit?" + query({ page, pageSize, search }),
  );
  return (
    <>
      <PageHeading
        title="Audit"

        action={
          <button className="secondary" onClick={audit.refresh}>
            Refresh
          </button>
        }
      />
      <section className="panel">
        <form
          className="toolbar"
          onSubmit={(e) => {
            e.preventDefault();
            setPage(1);
            setSearch(draft);
          }}
        >
          <label className="field search">
            Search audit
            <input
              type="search"
              value={draft}
              maxLength={200}
              placeholder="Correlation, action, target, or reason"
              onChange={(e) => setDraft(e.target.value)}
            />
          </label>
          <label className="field">
            Events per page
            <select
              value={pageSize}
              onChange={(e) => {
                setPageSize(Number(e.target.value));
                setPage(1);
              }}
            >
              <option value={25}>25 events</option>
              <option value={50}>50 events</option>
              <option value={100}>100 events</option>
            </select>
          </label>
          <button className="secondary">Apply filters</button>
          {search && (
            <button
              type="button"
              className="quiet"
              onClick={() => {
                setDraft("");
                setSearch("");
                setPage(1);
              }}
            >
              Clear
            </button>
          )}
        </form>
        {audit.loading ? (
          <Loading />
        ) : audit.error ? (
          <ErrorNotice error={audit.error} onRetry={audit.refresh} />
        ) : (
          audit.data && (
            <>
              {audit.data.items.length ? (
                <div className="table-scroll">
                  <table className="audit-table">
                    <thead>
                      <tr>
                        <th>Time / actor</th>
                        <th>Action</th>
                        <th>Scope / target</th>
                        <th>Reason</th>
                        <th>Outcome</th>
                      </tr>
                    </thead>
                    <tbody>
                      {audit.data.items.map((event) => (
                        <tr key={event.id}>
                          <td>
                            {date(event.at)}
                            <span className="cell-sub">{event.actorName}</span>
                          </td>
                          <th scope="row">
                            <button
                              className="text-button"
                              onClick={() => setSelected(event)}
                            >
                              {event.action}
                            </button>
                            <span className="cell-sub">
                              <code>{event.correlationId}</code>
                            </span>
                          </th>
                          <td>
                            {event.projectId || "Organization"}
                            <span className="cell-sub">
                              <code>{event.targetId}</code>
                            </span>
                            <span className="cell-sub">
                              Account revision{" "}
                              {event.accountRevision ?? "Not recorded"}
                            </span>
                          </td>
                          <td className="reason-cell">{event.reason || "—"}</td>
                          <td>
                            <Status value={event.outcome} />
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <Empty title="No matching events">
                  Try another search. An empty history does not mean a failed
                  request granted access.
                </Empty>
              )}
              <Pagination
                page={page}
                total={audit.data.total}
                pageSize={audit.data.pageSize}
                onChange={setPage}
              />
            </>
          )
        )}
      </section>
      <p className="footnote">
        The application exposes a read-only audit history. This is not a claim
        of tamper-proof storage against a database administrator.
      </p>
      {selected && (
        <Modal title="Audit event" onClose={() => setSelected(undefined)}>
          <dl className="facts">
            <div>
              <dt>Action</dt>
              <dd>{selected.action}</dd>
            </div>
            <div>
              <dt>Actor</dt>
              <dd>{selected.actorName}</dd>
            </div>
            <div>
              <dt>Time</dt>
              <dd>{date(selected.at)}</dd>
            </div>
            <div>
              <dt>Outcome</dt>
              <dd>
                <Status value={selected.outcome} />
              </dd>
            </div>
            <div>
              <dt>Account revision</dt>
              <dd>{selected.accountRevision ?? "Not recorded"}</dd>
            </div>
            <div>
              <dt>Target</dt>
              <dd>
                <code>{selected.targetId}</code>
              </dd>
            </div>
            <div>
              <dt>Correlation</dt>
              <dd>
                <code>{selected.correlationId}</code>
              </dd>
            </div>
          </dl>
          <h3>Reason</h3>
          <p className="quoted">{selected.reason || "No reason recorded."}</p>
          <Link
            className="text-link"
            to={
              "/requests?search=" + encodeURIComponent(selected.correlationId)
            }
          >
            Find related requests →
          </Link>
        </Modal>
      )}
    </>
  );
}
