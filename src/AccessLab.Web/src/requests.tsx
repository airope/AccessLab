import { useEffect, useRef, useState, type FormEvent } from "react";
import { AcceptedContext } from "./decision-context";
import { ApiError, query } from "./api";
import type {
  Account,
  AccessRequest,
  ChangeCommand,
  Group,
  Membership,
  Page,
  Preview,
  Session,
} from "./types";
import { hasRole } from "./types";
import {
  date,
  Empty,
  ErrorNotice,
  ImpactPreview,
  PreviewConfirmation,
  Link,
  Loading,
  navigate,
  PageHeading,
  Pagination,
  ReasonField,
  Status,
  useLoad,
  useMutation,
} from "./ui";
export function RequestsPage() {
  const [groupId, setGroupId] = useState(
    new URLSearchParams(location.search).get("groupId") || "",
  );
  const initialSearch =
    new URLSearchParams(location.search).get("search") || "";
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState(initialSearch);
  const [draft, setDraft] = useState(initialSearch);
  const [status, setStatus] = useState("");
  const requests = useLoad<Page<AccessRequest>>(
    "/api/requests?" + query({ page, pageSize: 25, search, status, groupId }),
  );
  return (
    <>
      <PageHeading
        title="Requests"

        action={
          <Link to="/requests/new" className="button">
            Request access <span aria-hidden="true">↗</span>
          </Link>
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
            Search requests
            <input
              type="search"
              maxLength={200}
              placeholder="Request ID, correlation ID, or reason"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
            />
          </label>
          <label className="field">
            Status
            <select
              value={status}
              onChange={(e) => {
                setStatus(e.target.value);
                setPage(1);
              }}
            >
              <option value="">All statuses</option>
              {["Pending", "Approved", "Rejected", "Cancelled", "Expired"].map(
                (s) => (
                  <option key={s}>{s}</option>
                ),
              )}
            </select>
          </label>
          <button className="secondary">Search</button>
          {groupId && (
            <span className="context-line">
              Group: <code>{groupId}</code>{" "}
              <button
                type="button"
                className="quiet"
                onClick={() => {
                  setGroupId("");
                  setPage(1);
                  history.replaceState({}, "", "/requests");
                }}
              >
                Clear group filter
              </button>
            </span>
          )}
          <button type="button" className="quiet" onClick={requests.refresh}>
            Refresh
          </button>
        </form>
        {requests.loading ? (
          <Loading />
        ) : requests.error ? (
          <ErrorNotice error={requests.error} onRetry={requests.refresh} />
        ) : (
          requests.data && (
            <>
              {requests.data.items.length ? (
                <div className="table-scroll">
                  <table className="requests-table">
                    <thead>
                      <tr>
                        <th>Change</th>
                        <th>Beneficiary</th>
                        <th>Requested period</th>
                        <th>Decision / application</th>
                        <th>
                          <span className="sr-only">Open request</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {requests.data.items.map((r) => (
                        <tr key={r.id}>
                          <th scope="row">
                            {r.operation === "Add" ? "Join" : "Leave"}{" "}
                            {r.groupName}
                            <span className="cell-sub">
                              {r.projectId} · {r.actorName}
                            </span>
                          </th>
                          <td>{r.accountName}</td>
                          <td>
                            {date(r.startsAt)}
                            <span className="cell-sub">
                              until {date(r.endsAt)}
                            </span>
                          </td>
                          <td>
                            <Status value={r.status} />
                            {r.syncStatus && (
                              <span className="cell-sub">
                                <Status value={r.syncStatus} />
                              </span>
                            )}
                          </td>
                          <td>
                            <Link
                              className="text-link"
                              to={"/requests/" + encodeURIComponent(r.id)}
                            >
                              Review{" "}
                              <span className="sr-only">
                                request for {r.accountName}
                              </span>
                              →
                            </Link>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <Empty title="No matching requests">
                  Try another filter or request a temporary group membership.
                </Empty>
              )}
              <Pagination
                page={page}
                total={requests.data.total}
                pageSize={requests.data.pageSize}
                onChange={setPage}
              />
            </>
          )
        )}
      </section>
    </>
  );
}
export function RequestForm({ session }: { session: Session }) {
  const maxMembershipDays = session.maxMembershipDays ?? 90;
  const groups = useLoad<{ items: Group[] }>("/api/groups");
  const [accountPage, setAccountPage] = useState(1);
  const accounts = useLoad<Page<Account>>(
    "/api/accounts?" + query({ page: accountPage, pageSize: 100 }),
  );
  const [accountId, setAccountId] = useState(session.accountId);
  const [selectedAccount, setSelectedAccount] = useState<Account>();
  const [groupId, setGroupId] = useState("");
  const [operation, setOperation] = useState("Add");
  const [timing, setTiming] = useState("immediate");
  const [startsAt, setStartsAt] = useState("");
  const [endsAt, setEndsAt] = useState("");
  const [reason, setReason] = useState("");
  const [membershipId, setMembershipId] = useState("");
  const [membershipPage, setMembershipPage] = useState(1);
  const [preview, setPreview] = useState<Preview>();
  const [validation, setValidation] = useState<Error>();
  const mutation = useMutation();
  const command = useRef<ChangeCommand | undefined>(undefined);
  const group = groups.data?.items.find((g) => g.id === groupId);
  const members = useLoad<Page<Membership>>(
    operation === "Remove" && groupId
      ? "/api/memberships?" +
          query({ accountId, groupId, page: membershipPage, pageSize: 25 })
      : null,
  );
  const target =
    accounts.data?.items.find((a) => a.id === accountId) || selectedAccount;
  const targetProjectIds = target?.projectIds || session.projectIds;
  const possibleGroups =
    groups.data?.items.filter(
      (g) =>
        (operation === "Remove" || g.requestable) &&
        targetProjectIds.includes(g.projectId),
    ) || [];
  function edit() {
    setPreview(undefined);
    setValidation(undefined);
    command.current = undefined;
    mutation.clear();
  }
  async function calculate(e?: FormEvent) {
    e?.preventDefault();
    setValidation(undefined);
    setPreview(undefined);
    if (!command.current) {
      const start =
        timing === "immediate" || operation === "Remove"
          ? new Date().toISOString()
          : new Date(startsAt + "Z").toISOString();
      const end = endsAt ? new Date(endsAt + "Z").toISOString() : start;
      if (
        !groupId ||
        !reason.trim() ||
        (operation === "Add" &&
          (!endsAt ||
            new Date(end) <= new Date(start) ||
            new Date(end).getTime() - new Date(start).getTime() >
              maxMembershipDays * 86400000))
      ) {
        setValidation(
          new Error(
            `Choose a group and provide a reason. The end must follow the start, within ${maxMembershipDays} days.`,
          ),
        );
        return;
      }
      command.current = {
        accountId,
        groupId,
        operation,
        startsAt: start,
        endsAt: end,
        reason,
        ...(membershipId ? { membershipId } : {}),
      };
    }
    const result = await mutation.run<Preview>(
      "/api/previews",
      command.current,
    );
    if (result) setPreview(result);
  }
  async function submit() {
    if (!preview) return;
    const result = await mutation.run<AccessRequest>("/api/requests", {
      previewId: preview.id,
    });
    if (result) navigate("/requests/" + encodeURIComponent(result.id));
  }
  useEffect(() => {
    if (mutation.error instanceof ApiError && mutation.error.status === 409)
      setPreview(undefined);
  }, [mutation.error]);
  const error = validation || mutation.error;
  return (
    <>
      <Link to="/requests" className="back-link">
        ← All requests
      </Link>
      <PageHeading title="Request access" />
      <div className="form-layout">
        <div>
          {groups.loading || accounts.loading ? (
            <Loading />
          ) : groups.error || accounts.error ? (
            <ErrorNotice
              error={(groups.error || accounts.error)!}
              onRetry={() => {
                groups.refresh();
                accounts.refresh();
              }}
            />
          ) : (
            <form className="panel form-panel" onSubmit={calculate}>
              <div className="section-heading">
                <h2>Change details</h2>
              </div>
              <fieldset disabled={mutation.busy || !!preview}>
                <label className="field">
                  Beneficiary
                  <select
                    value={accountId}
                    onChange={(e) => {
                      edit();
                      setAccountId(e.target.value);
                      setSelectedAccount(
                        accounts.data?.items.find(
                          (a) => a.id === e.target.value,
                        ),
                      );
                      setGroupId("");
                      setMembershipId("");
                      setMembershipPage(1);
                    }}
                  >
                    <option value={session.accountId}>
                      {session.displayName} (you)
                    </option>
                    {selectedAccount &&
                      selectedAccount.id !== session.accountId &&
                      !accounts.data?.items.some(
                        (a) => a.id === selectedAccount.id,
                      ) && (
                        <option value={selectedAccount.id}>
                          {selectedAccount.displayName}
                        </option>
                      )}
                    {hasRole(session, "Reviewer") &&
                      accounts.data?.items
                        .filter(
                          (a) =>
                            a.id !== session.accountId && a.status === "Active",
                        )
                        .map((a) => (
                          <option key={a.id} value={a.id}>
                            {a.displayName}
                          </option>
                        ))}
                  </select>
                </label>
                {hasRole(session, "Reviewer") &&
                  accounts.data &&
                  accounts.data.total > 100 && (
                    <Pagination
                      page={accountPage}
                      total={accounts.data.total}
                      pageSize={100}
                      onChange={setAccountPage}
                    />
                  )}
                <div className="field-pair">
                  <label className="field">
                    Operation
                    <select
                      value={operation}
                      onChange={(e) => {
                        edit();
                        setOperation(e.target.value);
                        setMembershipId("");
                        setMembershipPage(1);
                      }}
                    >
                      <option value="Add">Add membership</option>
                      <option value="Remove">Remove membership</option>
                    </select>
                  </label>
                  <label className="field">
                    Group
                    <select
                      required
                      value={groupId}
                      onChange={(e) => {
                        edit();
                        setGroupId(e.target.value);
                        setMembershipId("");
                        setMembershipPage(1);
                      }}
                    >
                      <option value="">Choose a group</option>
                      {possibleGroups.map((g) => (
                        <option key={g.id} value={g.id}>
                          {g.name} · {g.projectId}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>
                {group && (
                  <p className="context-line">
                    {group.description}
                    <br />
                    <span className="hint">
                      Potential resources:{" "}
                      {group.resourceIds.join(", ") || "None"}
                    </span>
                  </p>
                )}
                {operation === "Remove" && (
                  <>
                    {members.loading ? (
                      <Loading />
                    ) : members.error ? (
                      <ErrorNotice
                        error={members.error}
                        onRetry={members.refresh}
                      />
                    ) : (
                      <label className="field">
                        Membership
                        <select
                          required
                          value={membershipId}
                          onChange={(e) => {
                            edit();
                            setMembershipId(e.target.value);
                          }}
                        >
                          <option value="">Choose the exact membership</option>
                          {members.data?.items
                            .filter(
                              (m) =>
                                m.accountId === accountId &&
                                m.groupId === groupId &&
                                m.status !== "Removed" &&
                                new Date(m.endsAt) > new Date(),
                            )
                            .map((m) => (
                              <option
                                key={m.membershipId}
                                value={m.membershipId}
                              >
                                {m.displayName} · {date(m.startsAt)} –{" "}
                                {date(m.endsAt)}
                              </option>
                            ))}
                        </select>
                      </label>
                    )}
                    {members.data &&
                      !members.loading &&
                      !members.error &&
                      members.data.total !== undefined && (
                        <Pagination
                          page={membershipPage}
                          total={members.data.total}
                          pageSize={members.data.pageSize}
                          onChange={(next) => {
                            edit();
                            setMembershipId("");
                            setMembershipPage(next);
                          }}
                        />
                      )}
                    <p className="hint">
                      Choose an unexpired membership in the selected group.
                    </p>
                  </>
                )}
                {operation === "Add" ? (
                  <label className="field">
                    When should this change start?
                    <select
                      value={timing}
                      onChange={(e) => {
                        edit();
                        setTiming(e.target.value);
                      }}
                    >
                      <option value="immediate">As soon as approved</option>
                      <option value="scheduled">At a scheduled date</option>
                    </select>
                  </label>
                ) : (
                  <p className="context-line">
                    Removal takes effect when approved. The selected
                    membership’s original period is retained in the request
                    record; it is not rescheduled.
                  </p>
                )}
                <div className="field-pair">
                  {operation === "Add" && timing === "scheduled" && (
                    <label className="field">
                      Start date
                      <input
                        aria-label="Start date"
                        type="datetime-local"
                        required
                        value={startsAt}
                        onChange={(e) => {
                          edit();
                          setStartsAt(e.target.value);
                        }}
                      />
                      <span className="hint">UTC · inclusive</span>
                    </label>
                  )}
                  {operation === "Add" && (
                    <label className="field">
                      End date
                      <input
                        aria-label="End date"
                        type="datetime-local"
                        required
                        value={endsAt}
                        onChange={(e) => {
                          edit();
                          setEndsAt(e.target.value);
                        }}
                      />
                      <span className="hint">
                        UTC · exclusive · maximum {maxMembershipDays} days
                      </span>
                    </label>
                  )}
                </div>
                <ReasonField
                  value={reason}
                  onChange={(v) => {
                    edit();
                    setReason(v);
                  }}
                />
              </fieldset>
              {!preview && (
                <div className="action-footer">
                  <button disabled={mutation.busy}>
                    {mutation.busy
                      ? "Calculating…"
                      : error instanceof ApiError && error.status === 409
                        ? "Refresh impact"
                        : "Preview impact"}
                  </button>
                </div>
              )}
              {preview && (
                <button type="button" className="secondary" onClick={edit}>
                  Edit arguments
                </button>
              )}
            </form>
          )}
          {error && <ErrorNotice error={error} />}{" "}
          {preview && (
            <div className="panel form-panel">
              <ImpactPreview preview={preview} />
              <div className="action-footer">
                <p>
                  A request does not activate access. A separate reviewer must
                  approve it.
                </p>
                <PreviewConfirmation
                  expiresAt={preview.expiresAt}
                  busy={mutation.busy}
                  label="Submit request"
                  onConfirm={() => void submit()}
                  onRefresh={() => void calculate()}
                />
              </div>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
export function RequestReview({ id }: { id: string }) {
  const request = useLoad<AccessRequest>(
    "/api/requests/" + encodeURIComponent(id),
  );
  const mutation = useMutation();
  const [preview, setPreview] = useState<Preview>();
  const [reason, setReason] = useState("");
  const [decision, setDecision] = useState<"reject" | "cancel">();
  const [completed, setCompleted] = useState<AccessRequest>();
  const r = completed || request.data;
  async function refreshImpact() {
    setPreview(undefined);
    const result = await mutation.run<Preview>(
      "/api/requests/" + encodeURIComponent(id) + "/preview",
      {},
    );
    if (result) setPreview(result);
  }
  async function approve() {
    if (!preview) return;
    const result = await mutation.run<AccessRequest>(
      "/api/requests/" + encodeURIComponent(id) + "/approve",
      { previewId: preview.id },
    );
    if (result) {
      setCompleted(result);
      setPreview(undefined);
    }
  }
  async function decide(e: FormEvent) {
    e.preventDefault();
    if (!decision) return;
    const result = await mutation.run<AccessRequest>(
      "/api/requests/" + encodeURIComponent(id) + "/" + decision,
      { reason },
    );
    if (result) {
      setCompleted(result);
      setDecision(undefined);
      setPreview(undefined);
    }
  }
  useEffect(() => {
    if (!(mutation.error instanceof ApiError) || mutation.error.status !== 409)
      return;
    setPreview(undefined);
    if (mutation.error.code === "request_terminal") {
      setDecision(undefined);
      setReason("");
      setCompleted(undefined);
      mutation.clear();
      request.refresh();
    }
  }, [mutation.error]);
  return (
    <>
      <Link to="/requests" className="back-link">
        ← All requests
      </Link>
      {request.loading ? (
        <Loading />
      ) : request.error ? (
        <ErrorNotice error={request.error} onRetry={request.refresh} />
      ) : (
        r && (
          <>
            <PageHeading
              title={
                r.operation === "Add"
                  ? "Join " + r.groupName
                  : "Leave " + r.groupName
              }
            />
            <div className="review-layout">
              <section className="panel form-panel">
                <div className="section-heading">
                  <h2>Requested change</h2>
                  <Status value={r.status} />
                </div>
                <dl className="facts">
                  <div>
                    <dt>Project</dt>
                    <dd>{r.projectId}</dd>
                  </div>
                  <div>
                    <dt>Beneficiary</dt>
                    <dd>{r.accountName}</dd>
                  </div>
                  <div>
                    <dt>Submitted by</dt>
                    <dd>{r.actorName}</dd>
                  </div>
                  <div>
                    <dt>Start date</dt>
                    <dd>{date(r.startsAt)}</dd>
                  </div>
                  <div>
                    <dt>End date</dt>
                    <dd>{date(r.endsAt)}</dd>
                  </div>
                  <div>
                    <dt>Submitted</dt>
                    <dd>{date(r.createdAt)}</dd>
                  </div>
                  <div>
                    <dt>Request expires</dt>
                    <dd>{date(r.expiresAt)}</dd>
                  </div>
                </dl>
                <h3>Reason</h3>
                <p className="quoted">{r.reason}</p>
                {r.decisionReason && (
                  <>
                    <h3>Decision reason</h3>
                    <p className="quoted">{r.decisionReason}</p>
                  </>
                )}
                {r.decidedBy && (
                  <p>
                    Decided by <code>{r.decidedBy}</code>
                  </p>
                )}
                {r.syncStatus && (
                  <div className="context-line">
                    <span>Application </span>
                    <Status value={r.syncStatus} />
                    <p className="hint">
                      {r.syncStatus === "InSync"
                        ? "The current desired revision is observed in the simulator."
                        : "Approved does not mean applied. Check My access or Operations for current observed state."}
                    </p>
                  </div>
                )}
                <AcceptedContext request={r} />
                <div className="metadata">
                  <span>
                    Request <code>{r.id}</code>
                  </span>
                  <span>
                    Correlation <code>{r.correlationId}</code>
                  </span>
                </div>
              </section>
              <section className="panel form-panel">
                <h2>
                  {r.status === "Pending"
                    ? "Review impact before approving"
                    : "Decision recorded"}
                </h2>
                {r.status === "Pending" ? (
                  <>
                    <p className="muted">
                      The author and beneficiary cannot approve their own
                      request. Server permissions and current state are
                      rechecked on every decision.
                    </p>
                    {mutation.error && <ErrorNotice error={mutation.error} />}{" "}
                    {r.canReview &&
                      (!preview ||
                        (mutation.error instanceof ApiError &&
                          mutation.error.status === 409)) && (
                        <button
                          disabled={mutation.busy}
                          onClick={() => void refreshImpact()}
                        >
                          {mutation.busy
                            ? "Calculating…"
                            : mutation.error instanceof ApiError &&
                                mutation.error.status === 409
                              ? "Refresh impact"
                              : "Preview impact"}
                        </button>
                      )}
                    {preview &&
                      !(
                        mutation.error instanceof ApiError &&
                        mutation.error.status === 409
                      ) && (
                        <>
                          <ImpactPreview preview={preview} />
                          <PreviewConfirmation
                            expiresAt={preview.expiresAt}
                            busy={mutation.busy}
                            label="Approve"
                            onConfirm={() => void approve()}
                            onRefresh={() => void refreshImpact()}
                          />
                        </>
                      )}
                    {!r.canReview && (
                      <p className="notice neutral">
                        This request cannot be approved by your account. It
                        needs an independent reviewer in the same project.
                      </p>
                    )}
                    {!decision ? (
                      <div className="actions decision-actions">
                        {r.canReview && (
                          <button
                            className="danger-secondary"
                            disabled={mutation.busy}
                            onClick={() => setDecision("reject")}
                          >
                            Reject
                          </button>
                        )}
                        {r.canCancel && (
                          <button
                            className="secondary"
                            disabled={mutation.busy}
                            onClick={() => setDecision("cancel")}
                          >
                            Cancel request
                          </button>
                        )}
                      </div>
                    ) : (
                      <form className="decision-form" onSubmit={decide}>
                        <h3>
                          {decision === "reject"
                            ? "Reject this request"
                            : "Cancel this request"}
                        </h3>
                        <ReasonField value={reason} onChange={setReason} />
                        <div className="actions">
                          <button className="danger" disabled={mutation.busy}>
                            {mutation.busy
                              ? "Recording…"
                              : decision === "reject"
                                ? "Confirm rejection"
                                : "Confirm cancellation"}
                          </button>
                          <button
                            type="button"
                            className="quiet"
                            disabled={mutation.busy}
                            onClick={() => setDecision(undefined)}
                          >
                            Keep reviewing
                          </button>
                        </div>
                      </form>
                    )}
                  </>
                ) : (
                  <>
                    <p>
                      This request is {r.status.toLowerCase()} and cannot be
                      edited.
                    </p>
                    <Link to="/audit" className="text-link">
                      Explore the audit trail →
                    </Link>
                    <div className="action-footer">
                      <button
                        className="secondary"
                        onClick={() => {
                          setCompleted(undefined);
                          request.refresh();
                        }}
                      >
                        Refresh application status
                      </button>
                    </div>
                  </>
                )}
              </section>
            </div>
          </>
        )
      )}
    </>
  );
}
