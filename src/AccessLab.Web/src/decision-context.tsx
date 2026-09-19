import type { AccessRequest } from "./types";
import { date } from "./ui";
export function AcceptedContext({ request: r }: { request: AccessRequest }) {
  const c = r.decisionContext;
  if (!c && !r.acceptedPreviewId) return null;
  return (
    <section aria-label="Accepted decision context">
      <h3>Accepted decision context</h3>
      <p className="hint">
        Historical, read-only snapshot—not a fresh approval or proof of current
        access.
      </p>
      <dl className="facts">
        <div>
          <dt>Accepted preview</dt>
          <dd>
            <code>{r.acceptedPreviewId || "Not recorded"}</code>
          </dd>
        </div>
        {c && (
          <>
            <div>
              <dt>Account revision</dt>
              <dd>{c.accountRevision}</dd>
            </div>
            <div>
              <dt>Rule version</dt>
              <dd>{c.ruleVersion}</dd>
            </div>
            <div>
              <dt>Dependency fingerprint</dt>
              <dd>
                <code>{c.fingerprint}</code>
              </dd>
            </div>
            <div>
              <dt>Calculated at</dt>
              <dd>{date(c.calculatedAt)}</dd>
            </div>
            <div>
              <dt>Preview expired at</dt>
              <dd>{date(c.expiresAt)}</dd>
            </div>
            <div>
              <dt>Accepted operation</dt>
              <dd>{c.command.operation}</dd>
            </div>
            <div>
              <dt>Accepted account</dt>
              <dd>
                <code>{c.command.accountId}</code>
              </dd>
            </div>
            <div>
              <dt>Accepted group</dt>
              <dd>
                <code>{c.command.groupId}</code>
              </dd>
            </div>
            <div>
              <dt>Accepted start</dt>
              <dd>{date(c.command.startsAt)}</dd>
            </div>
            <div>
              <dt>Accepted end</dt>
              <dd>{date(c.command.endsAt)}</dd>
            </div>
            {c.command.membershipId && (
              <div>
                <dt>Accepted membership</dt>
                <dd>
                  <code>{c.command.membershipId}</code>
                </dd>
              </div>
            )}
          </>
        )}
      </dl>
      {c && <p className="quoted">{c.command.reason}</p>}
    </section>
  );
}
