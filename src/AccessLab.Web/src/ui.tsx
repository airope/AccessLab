import { useEffect, useRef, useState, type ReactNode } from "react";
import { api, ApiError } from "./api";
import type { Change, Preview } from "./types";
export const date = (value?: string | null) =>
  value
    ? new Intl.DateTimeFormat("en-GB", {
        dateStyle: "medium",
        timeStyle: "short",
        timeZone: "UTC",
      }).format(new Date(value)) + " UTC"
    : "—";
export function useLoad<T>(path: string | null, pollMs = 0) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<Error>();
  const [loading, setLoading] = useState(true);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    if (!path) {
      setLoading(false);
      setData(undefined);
      return;
    }
    let alive = true;
    let inFlight = false;
    const controller = new AbortController();
    setLoading(true);
    setError(undefined);
    setData(undefined);
    const load = async () => {
      if (inFlight || !alive) return;
      inFlight = true;
      try {
        const result = await api.get<T>(path, controller.signal);
        if (alive) {
          setData(result);
          setError(undefined);
        }
      } catch (e) {
        if (alive) setError(e as Error);
      } finally {
        inFlight = false;
        if (alive) setLoading(false);
      }
    };
    void load();
    const timer = pollMs
      ? setInterval(() => {
          if (document.visibilityState === "visible") void load();
        }, pollMs)
      : undefined;
    return () => {
      alive = false;
      controller.abort();
      if (timer) clearInterval(timer);
    };
  }, [path, revision, pollMs]);
  return { data, error, loading, refresh: () => setRevision((n) => n + 1) };
}
export function useMutation() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();
  const lock = useRef(false);
  const attempt = useRef<{ fingerprint: string; key: string } | null>(null);
  async function run<T>(path: string, body: unknown): Promise<T | undefined> {
    if (lock.current) return;
    lock.current = true;
    setBusy(true);
    setError(undefined);
    const fingerprint = JSON.stringify([path, body]);
    if (attempt.current?.fingerprint !== fingerprint)
      attempt.current = { fingerprint, key: crypto.randomUUID() };
    try {
      const result = await api.post<T>(path, body, attempt.current.key);
      attempt.current = null;
      return result;
    } catch (e) {
      setError(e as Error);
      return undefined;
    } finally {
      lock.current = false;
      setBusy(false);
    }
  }
  return {
    busy,
    error,
    run,
    clear: () => {
      setError(undefined);
      attempt.current = null;
    },
  };
}
const errorTitles: Record<number, string> = {
  0: "Connection interrupted",
  401: "Your session has expired",
  403: "This action is not permitted",
  404: "This item is not available",
  409: "The context has changed",
  503: "The service is temporarily unavailable",
};
export function ErrorNotice({
  error,
  onRetry,
  retryLabel = "Try again",
}: {
  error: Error;
  onRetry?: () => void;
  retryLabel?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, [error]);
  const status = error instanceof ApiError ? error.status : 0;
  return (
    <div className="notice error" role="alert" tabIndex={-1} ref={ref}>
      <strong>{errorTitles[status] || "Unable to complete this action"}</strong>
      <p>{error.message}</p>
      {status === 409 && (
        <p>
          Your original arguments have not changed. Refresh the impact and
          review it before confirming again.
        </p>
      )}
      {error instanceof ApiError && error.correlationId && (
        <p className="meta">
          Correlation <code>{error.correlationId}</code>
        </p>
      )}
      <div className="actions">
        {status === 401 && (
          <a
            className="button"
            href={
              "/auth/login?returnUrl=" +
              encodeURIComponent(location.pathname + location.search)
            }
          >
            Sign in again
          </a>
        )}
        {onRetry && (
          <button className="secondary" onClick={onRetry}>
            {retryLabel}
          </button>
        )}
      </div>
    </div>
  );
}
export function Loading() {
  return (
    <div role="status" className="loading">
      <span className="spinner" />
      Loading from AccessLab…
    </div>
  );
}
export function Empty({
  title,
  children,
}: {
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className="empty">
      <h3>{title}</h3>
      {children && <p>{children}</p>}
    </div>
  );
}
export function Status({ value }: { value: string | null | undefined }) {
  const label = value === "InSync" ? "In sync" : value || "Not available";
  return (
    <span className={"status status-" + (value || "unknown").toLowerCase()}>
      {label}
    </span>
  );
}
export function PageHeading({
  title,
  action,
}: {
  title: string;
  action?: ReactNode;
}) {
  return (
    <header className="page-heading">
      <h1>{title}</h1>
      {action && <div className="actions">{action}</div>}
    </header>
  );
}
export function Pagination({
  page,
  total,
  pageSize,
  onChange,
}: {
  page: number;
  total: number;
  pageSize: number;
  onChange: (page: number) => void;
}) {
  return (
    <div className="pagination">
      <span>
        {total
          ? `${(page - 1) * pageSize + 1}–${Math.min(page * pageSize, total)} of ${total}`
          : "0 results"}
      </span>
      <div className="actions">
        <button
          type="button"
          className="secondary"
          disabled={page <= 1}
          onClick={() => onChange(page - 1)}
        >
          Previous
        </button>
        <span>Page {page}</span>
        <button
          type="button"
          className="secondary"
          disabled={page * pageSize >= total}
          onClick={() => onChange(page + 1)}
        >
          Next
        </button>
      </div>
    </div>
  );
}
export function Changes({ changes }: { changes: Change[] }) {
  return changes.length ? (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            <th>Resource</th>
            <th>Effect</th>
            <th>Explanation</th>
          </tr>
        </thead>
        <tbody>
          {changes.map((c) => (
            <tr key={c.resourceId}>
              <th scope="row">{c.resourceName}</th>
              <td>
                <Status value={c.effect} />
              </td>
              <td>
                {c.reasons.map((r, i) => (
                  <p key={i}>{r}</p>
                ))}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  ) : (
    <p className="muted">No resource changes at this time.</p>
  );
}
export function ImpactPreview({ preview }: { preview: Preview }) {
  return (
    <section className="impact" aria-label="Server-calculated impact">
      <div className="section-heading">
        <h2>Review the exact impact</h2>
        <span className="meta">Account revision {preview.accountRevision}</span>
      </div>
      <p className="meta">
        Valid until {date(preview.expiresAt)}. A new calculation is required
        after expiry or a relevant change.
      </p>
      {preview.warnings.map((w, i) => (
        <p className="notice warning" key={i}>
          {w}
        </p>
      ))}
      <h3>Immediate impact</h3>
      <Changes changes={preview.changes} />
      {preview.scheduledChanges.length > 0 && (
        <>
          <h3>Scheduled impact</h3>
          {preview.scheduledChanges.map((s, i) => (
            <div key={i} className="scheduled">
              <h4>{date(s.at)}</h4>
              <Changes changes={s.changes} />
            </div>
          ))}
        </>
      )}
    </section>
  );
}
export function PreviewConfirmation({
  expiresAt,
  busy,
  label,
  onConfirm,
  onRefresh,
}: {
  expiresAt: string;
  busy: boolean;
  label: string;
  onConfirm: () => void;
  onRefresh: () => void;
}) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const expired = new Date(expiresAt).getTime() <= now;
  return (
    <div className="preview-confirmation">
      {expired && (
        <p className="notice warning" role="status">
          This preview has expired. Refresh the impact and review the new
          calculation before confirming.
        </p>
      )}
      <div className="actions decision-actions">
        {!expired && (
          <button disabled={busy} onClick={onConfirm}>
            {busy ? "Working…" : label}
          </button>
        )}
        <button className="secondary" disabled={busy} onClick={onRefresh}>
          Refresh impact
        </button>
      </div>
    </div>
  );
}
export function Modal({
  title,
  children,
  onClose,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const prior = document.activeElement as HTMLElement;
    ref.current?.showModal?.();
    return () => {
      prior?.focus();
    };
  }, []);
  return (
    <dialog
      ref={ref}
      aria-label={title}
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
    >
      <div className="dialog-heading">
        <h2>{title}</h2>
        <button className="quiet" aria-label="Close dialog" onClick={onClose}>
          Close ×
        </button>
      </div>
      {children}
    </dialog>
  );
}
export function ReasonField({
  value,
  onChange,
  label = "Reason",
}: {
  value: string;
  onChange: (value: string) => void;
  label?: string;
}) {
  return (
    <label className="field">
      {label}
      <textarea
        aria-label={label}
        required
        minLength={3}
        maxLength={1000}
        rows={3}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Explain why this change is needed."
      />
      <span className="hint">
        Required. Use fictional information only; do not include credentials.
      </span>
    </label>
  );
}
export function Link({
  to,
  children,
  className,
}: {
  to: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <a
      href={to}
      className={className}
      onClick={(e) => {
        if (
          e.button === 0 &&
          !e.metaKey &&
          !e.ctrlKey &&
          !e.shiftKey &&
          !e.altKey
        ) {
          e.preventDefault();
          navigate(to);
        }
      }}
    >
      {children}
    </a>
  );
}
export function navigate(to: string) {
  history.pushState({}, "", to);
  window.dispatchEvent(new PopStateEvent("popstate"));
  window.scrollTo?.(0, 0);
}
