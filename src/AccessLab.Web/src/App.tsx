import {
  Component,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { SessionGate, Logout } from "./session";
import { AccessPage } from "./access";
import { RequestForm, RequestReview, RequestsPage } from "./requests";
import { GroupsPage, AccountsPage } from "./directory";
import { AuditPage, OperationsPage } from "./operations";
import { hasRole, type Session } from "./types";
import { Empty, Link } from "./ui";
export function App() {
  return (
    <AppBoundary>
      <SessionGate>{(session) => <Workspace session={session} />}</SessionGate>
    </AppBoundary>
  );
}
function Workspace({ session }: { session: Session }) {
  const [url, setUrl] = useState(location.pathname + location.search);
  const [mobileOpen, setMobileOpen] = useState(false);
  const main = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    if (location.pathname === "/") {
      history.replaceState({}, "", "/access");
      setUrl("/access");
    }
    const onNavigate = () => {
      setUrl(location.pathname + location.search);
      setMobileOpen(false);
    };
    window.addEventListener("popstate", onNavigate);
    return () => window.removeEventListener("popstate", onNavigate);
  }, []);
  useEffect(() => {
    main.current?.focus();
    document.title =
      (url.split("/")[1]?.split("?")[0] || "Access") + " · AccessLab";
  }, [url]);
  const path = url.split("?")[0];
  const operator = hasRole(session, "Operator");
  const nav = [
    ["/access", "My access", "◉"],
    ["/requests", "Requests", "↗"],
    ["/groups", "Groups", "⊞"],
    ["/accounts", "Accounts", "⊙"],
    ...(operator ? [["/operations", "Operations", "⇄"]] : []),
    ["/audit", "Audit", "≡"],
  ];
  let page: ReactNode;
  if (path === "/" || path === "/access")
    page = (
      <AccessPage
        accountId={
          new URLSearchParams(url.split("?")[1]).get("accountId") || undefined
        }
      />
    );
  else if (path === "/requests/new") page = <RequestForm session={session} />;
  else if (/^\/requests\/[^/]+$/.test(path))
    page = <RequestReview id={decodeURIComponent(path.split("/")[2])} />;
  else if (path === "/requests") page = <RequestsPage />;
  else if (path === "/groups") page = <GroupsPage session={session} />;
  else if (path === "/accounts") page = <AccountsPage session={session} />;
  else if (path === "/operations") page = <OperationsPage session={session} />;
  else if (path === "/audit") page = <AuditPage />;
  else
    page = (
      <Empty title="Page not found">
        <Link to="/access">Return to My access</Link>
      </Empty>
    );
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">
        Skip to main content
      </a>
      <aside className={"sidebar " + (mobileOpen ? "mobile-open" : "")}>
        <div className="brand-row">
          <Link className="brand" to="/access">
            <span className="brand-mark" aria-hidden="true">
              a/
            </span>
            AccessLab
          </Link>
          <button
            className="mobile-toggle secondary"
            aria-expanded={mobileOpen}
            aria-controls="workspace-nav"
            onClick={() => setMobileOpen(!mobileOpen)}
          >
            {mobileOpen ? "Close menu" : "Menu"}
          </button>
        </div>
        <div className="sidebar-body">
          <nav id="workspace-nav" aria-label="Workspace">
            {nav.map(([to, label, icon]) => (
              <a
                href={to}
                key={to}
                aria-current={
                  path === to || path.startsWith(to + "/") ? "page" : undefined
                }
                onClick={(e) => {
                  if (!e.metaKey && !e.ctrlKey && !e.shiftKey && !e.altKey) {
                    e.preventDefault();
                    history.pushState({}, "", to);
                    window.dispatchEvent(new PopStateEvent("popstate"));
                    window.scrollTo(0, 0);
                  }
                }}
              >
                <span aria-hidden="true" className="nav-icon">
                  {icon}
                </span>
                {label}
              </a>
            ))}
          </nav>
          <div className="identity">
            <span className="avatar" aria-hidden="true">
              {session.displayName.slice(0, 1)}
            </span>
            <div>
              <strong>{session.displayName}</strong>
              <span>
                {session.roles
                  .map((r) => r.role)
                  .filter((r, i, a) => a.indexOf(r) === i)
                  .join(" · ")}
              </span>
            </div>
          </div>
          <Logout />
        </div>
      </aside>
      <div className="workspace">
        <div className="topbar">
          <span>
            Access management <span aria-hidden="true">/</span>{" "}
            <strong>{session.projectIds.join(" · ") || "Organization"}</strong>
          </span>
          <div className="workspace-context">
            {session.demoMode && (
              <span className="environment-label">
                Demo · Simulated destination
              </span>
            )}
            <span className="timezone-label">UTC</span>
          </div>
        </div>
        <main ref={main} id="main-content" tabIndex={-1} key={url}>
          {page}
        </main>
      </div>
    </div>
  );
}
class AppBoundary extends Component<
  { children: ReactNode },
  { failed: boolean }
> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  render() {
    return this.state.failed ? (
      <main className="auth-shell">
        <h1>Unable to display this page</h1>
        <p>
          No access decision was made by this error screen. Reload to read the
          current server state.
        </p>
        <button onClick={() => location.reload()}>Reload AccessLab</button>
      </main>
    ) : (
      this.props.children
    );
  }
}
