import { useState, type FormEvent } from "react";
import { query } from "./api";
import type { Account, Group, Membership, Page, Session } from "./types";
import { hasRole } from "./types";
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
export function GroupsPage({ session }: { session: Session }) {
  const groups = useLoad<{ items: Group[] }>("/api/groups");
  const [selected, setSelected] = useState<Group>();
  const [project, setProject] = useState("");
  return (
    <>
      <PageHeading
        title="Groups"

        action={
          <Link to="/requests/new" className="button">
            Request access
          </Link>
        }
      />
      <section className="panel">
        <div className="toolbar">
          <label className="field">
            Project
            <select
              value={project}
              onChange={(e) => setProject(e.target.value)}
            >
              <option value="">All visible projects</option>
              {[...new Set(groups.data?.items.map((g) => g.projectId))].map(
                (p) => (
                  <option key={p}>{p}</option>
                ),
              )}
            </select>
          </label>
          <button className="quiet" onClick={groups.refresh}>
            Refresh
          </button>
        </div>
        {groups.loading ? (
          <Loading />
        ) : groups.error ? (
          <ErrorNotice error={groups.error} onRetry={groups.refresh} />
        ) : groups.data?.items.length ? (
          <div className="group-list">
            {groups.data.items
              .filter((g) => !project || g.projectId === project)
              .map((g) => (
                <article className="group-row" key={g.id}>
                  <div>
                    <span className="project">{g.projectId}</span>
                    <h2>{g.name}</h2>
                    <p>{g.description}</p>
                    <p className="meta">
                      Resources: {g.resourceIds.join(", ") || "None"}
                    </p>
                  </div>
                  <div className="group-actions">
                    <Status
                      value={g.requestable ? "Requestable" : "Restricted"}
                    />
                    {hasRole(session, "Operator") ||
                    hasRole(session, "Reviewer", g.projectId) ||
                    hasRole(session, "Auditor", g.projectId) ? (
                      <button
                        className="secondary"
                        onClick={() => setSelected(g)}
                      >
                        View members{" "}
                        <span className="sr-only">of {g.name}</span>→
                      </button>
                    ) : (
                      <span className="hint">
                        Member directory is restricted
                      </span>
                    )}
                  </div>
                </article>
              ))}
          </div>
        ) : (
          <Empty title="No groups in your scope">
            Your project affiliation determines the groups available here.
          </Empty>
        )}
      </section>
      <p className="footnote">
        Business groups never grant administrative roles. Group membership has a
        bounded lifetime. Multiple groups may grant the same resource; removing
        one path does not necessarily remove access.
      </p>
      {selected && (
        <Members group={selected} onClose={() => setSelected(undefined)} />
      )}
    </>
  );
}
function Members({ group, onClose }: { group: Group; onClose: () => void }) {
  const [page, setPage] = useState(1);
  const members = useLoad<Page<Membership>>(
    "/api/groups/" +
      encodeURIComponent(group.id) +
      "/members?" +
      query({ page, pageSize: 25 }),
  );
  return (
    <Modal title={group.name + " members"} onClose={onClose}>
      <p className="muted">
        {group.projectId} · Server-scoped directory. Dates are UTC.
      </p>
      {members.loading ? (
        <Loading />
      ) : members.error ? (
        <ErrorNotice error={members.error} onRetry={members.refresh} />
      ) : members.data?.items.length ? (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Account</th>
                <th>Membership period</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {members.data.items.map((m) => (
                <tr key={m.membershipId}>
                  <th scope="row">
                    {m.displayName}
                    <span className="cell-sub">
                      <code>{m.membershipId}</code>
                    </span>
                  </th>
                  <td>
                    {date(m.startsAt)}
                    <span className="cell-sub">until {date(m.endsAt)}</span>
                  </td>
                  <td>
                    <Status value={m.status} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <Empty title="No memberships">
          There are no visible memberships in this group.
        </Empty>
      )}
      {members.data &&
        !members.loading &&
        !members.error &&
        members.data.total !== undefined && (
          <Pagination
            page={page}
            total={members.data.total}
            pageSize={members.data.pageSize}
            onChange={setPage}
          />
        )}
      <div className="action-footer">
        <Link
          className="text-link"
          to={"/requests?groupId=" + encodeURIComponent(group.id)}
        >
          Browse requests →
        </Link>
      </div>
    </Modal>
  );
}
export function AccountsPage({ session }: { session: Session }) {
  const [page, setPage] = useState(1);
  const accounts = useLoad<Page<Account>>(
    "/api/accounts?" + query({ page, pageSize: 25 }),
  );
  const [selected, setSelected] = useState<Account>();
  return (
    <>
      <PageHeading
        title="Accounts"

        action={
          <button className="secondary" onClick={accounts.refresh}>
            Refresh
          </button>
        }
      />
      <p className="notice neutral">
        Suspension and departure revoke local permissions immediately. They do
        not delete the external OIDC identity. Revocation at the simulated
        destination is tracked separately.
      </p>
      <section className="panel">
        {accounts.loading ? (
          <Loading />
        ) : accounts.error ? (
          <ErrorNotice error={accounts.error} onRetry={accounts.refresh} />
        ) : (
          accounts.data && (
            <>
              {accounts.data.items.length ? (
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Account</th>
                        <th>Projects</th>
                        <th>Status</th>
                        <th>Validity (UTC)</th>
                        <th>Revision</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {accounts.data.items.map((a) => (
                        <tr key={a.id}>
                          <th scope="row">
                            {a.displayName}
                            <span className="cell-sub">
                              <code>{a.id}</code>
                            </span>
                          </th>
                          <td>{a.projectIds.join(", ")}</td>
                          <td>
                            <Status value={a.status} />
                          </td>
                          <td>
                            {a.startsAt === undefined
                              ? "Not provided"
                              : a.startsAt === null
                                ? "No start limit"
                                : date(a.startsAt)}
                            <span className="cell-sub">
                              {a.endsAt === undefined
                                ? "End not provided"
                                : a.endsAt === null
                                  ? "No end limit"
                                  : "until " + date(a.endsAt)}
                            </span>
                            <span className="hint">
                              Start inclusive · end exclusive
                            </span>
                          </td>
                          <td className="numeric">{a.revision}</td>
                          <td>
                            <div className="actions">
                              <Link
                                className="text-link"
                                to={
                                  "/access?accountId=" +
                                  encodeURIComponent(a.id)
                                }
                              >
                                Inspect access
                              </Link>
                              {hasRole(session, "Operator") && (
                                <button
                                  className="secondary"
                                  aria-label={"Manage " + a.displayName}
                                  onClick={() => setSelected(a)}
                                >
                                  Manage
                                </button>
                              )}
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <Empty title="No visible accounts">
                  No accounts are available in your scope.
                </Empty>
              )}
              <Pagination
                page={page}
                total={accounts.data.total}
                pageSize={accounts.data.pageSize}
                onChange={setPage}
              />
            </>
          )
        )}
      </section>
      {selected && (
        <AccountLifecycle
          account={selected}
          onClose={() => setSelected(undefined)}
          onSaved={() => {
            setSelected(undefined);
            accounts.refresh();
          }}
        />
      )}
    </>
  );
}
function AccountLifecycle({
  account,
  onClose,
  onSaved,
}: {
  account: Account;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [status, setStatus] = useState(
    account.status === "Active" ? "Suspended" : "Active",
  );
  const [reason, setReason] = useState("");
  const mutation = useMutation();
  async function submit(e: FormEvent) {
    e.preventDefault();
    const result = await mutation.run<Account>(
      "/api/accounts/" + encodeURIComponent(account.id) + "/status",
      { status, reason },
    );
    if (result) onSaved();
  }
  return (
    <Modal
      title={"Manage " + account.displayName}
      onClose={() => {
        if (!mutation.busy) onClose();
      }}
    >
      <p>
        Current status: <Status value={account.status} />
      </p>
      {account.status === "Ended" ? (
        <p className="notice neutral">
          This account has ended. Departure is terminal; it cannot be
          reactivated.
        </p>
      ) : (
        <form onSubmit={submit}>
          <fieldset disabled={mutation.busy}>
            <label className="field">
              New status
              <select
                value={status}
                onChange={(e) => setStatus(e.target.value)}
              >
                {account.status !== "Active" && (
                  <option value="Active">
                    Active — resume valid memberships
                  </option>
                )}
                {account.status !== "Suspended" && (
                  <option value="Suspended">
                    Suspended — temporarily block access
                  </option>
                )}
                <option value="Ended">Ended — permanent departure</option>
              </select>
            </label>
            <p
              className={status === "Ended" ? "notice warning" : "context-line"}
            >
              {status === "Ended"
                ? "Permanent departure cannot be undone. All local access will be denied immediately."
                : status === "Suspended"
                  ? "This temporarily denies all local access, even with an existing authenticated session."
                  : "Resuming only reevaluates memberships that are still valid; it does not extend expired access."}
            </p>
            <ReasonField value={reason} onChange={setReason} />
          </fieldset>
          {mutation.error && <ErrorNotice error={mutation.error} />}
          <div className="action-footer">
            <button
              className={status === "Ended" ? "danger" : ""}
              disabled={mutation.busy}
            >
              {mutation.busy ? "Saving…" : "Confirm status change"}
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
