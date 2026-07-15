import { Fragment } from "react";
import { Link } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { MatrixRain } from "../components/MatrixRain";

const STACK = [
  {
    head: "// FRONTEND",
    badge: "SPA",
    items: ["React 18 + TypeScript", "Vite build & dev server", "React Router v6", "Hand-built SVG charts", "Zero UI-framework — bespoke CSS"],
  },
  {
    head: "// BACKEND",
    badge: "API",
    items: ["Python 3.12 · FastAPI", "SQLAlchemy 2.x ORM", "Alembic migrations", "Pydantic v2 validation", "PyMySQL driver"],
  },
  {
    head: "// DATA",
    badge: "RDBMS",
    items: ["MySQL 8.0 · utf8mb4", "Roles / permissions / identities", "Per-request authorization reads", "Versioned, seeded migrations"],
  },
  {
    head: "// PLATFORM",
    badge: "DEVOPS",
    items: ["Docker Compose (one command)", "GitHub Actions CI", "Caddy + Let's Encrypt (prod)", "OIDC-federated deploys (planned)"],
  },
];

const FLOW = [
  ["Browser", "React SPA, cookie-only session"],
  ["Edge", "Vite proxy (dev) / Caddy TLS (prod)"],
  ["API", "FastAPI · verify · authorize · serve"],
  ["Data", "MySQL 8 · roles read every request"],
];

export function TechnologyPage() {
  const { user } = useAuth();
  return (
    <div className="tech">
      <MatrixRain opacity={0.14} />
      <div className="scanlines" />

      <nav className="landing-nav">
        <Link to="/" className="brand">
          ◈ KINGFISHER
        </Link>
        <span className="spacer" />
        <Link to="/security">Security</Link>
        <Link to="/about">About</Link>
        <Link to={user ? "/dashboard" : "/login"}>{user ? "Console" : "Sign in"}</Link>
      </nav>

      <div className="tech-wrap">
        <header className="tech-hero">
          <div className="hero-eyebrow cursor">SYS://technology</div>
          <h1>
            Built the modern way — <span className="lit">agentically.</span>
          </h1>
          <p>
            Kingfisher is a full-stack access-control console designed, built, reviewed, and
            shipped through a multi-agent workflow: models handle the grind — code, tests, docs,
            adversarial review — while the architect steers intent. What follows is the actual
            stack under this running system.
          </p>
        </header>

        <div className="section-divider" style={{ padding: 0 }}>// THE STACK</div>
        <div className="tech-grid">
          {STACK.map((s) => (
            <div className="panel panel--brackets tech-card" key={s.head}>
              <div className="tc-head">
                <span>{s.head}</span>
                <span className="badge">{s.badge}</span>
              </div>
              <ul>
                {s.items.map((i) => (
                  <li key={i}>{i}</li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <div className="section-divider" style={{ padding: 0 }}>// THE MODEL</div>
        <div className="tech-grid">
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// ENGINE</span><span className="badge">CLAUDE</span></div>
            <div className="kv-inline">
              Built with <b>Claude Opus&nbsp;4.8</b> as the reasoning engine — an early, serious
              adopter of agentic development. Design, implementation, and hardening each ran as
              their own orchestrated pass.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// ORCHESTRATION</span><span className="badge">MULTI-AGENT</span></div>
            <ul>
              <li>Design: a judged panel of art directions → one build-ready system</li>
              <li>Build: parallel section drafters, integrated by hand</li>
              <li>Review: adversarial security agents that try to break the auth</li>
              <li>Verify: real tests + a live browser drive before every commit</li>
            </ul>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// PROOF</span><span className="badge">SHIPPING</span></div>
            <div className="kv-inline">
              The adversarial review isn't decoration: it caught real privilege-escalation bugs in
              this very RBAC and forced fixes — <b>the proof is running software, not slideware.</b>
            </div>
          </div>
        </div>

        <div className="section-divider" style={{ padding: 0 }}>// REQUEST PATH</div>
        <div className="flow">
          {FLOW.map(([n, d], i) => (
            <Fragment key={n}>
              <div className="flow-node">
                <div className="fn">{n}</div>
                <div className="fd">{d}</div>
              </div>
              {i < FLOW.length - 1 && <span className="flow-arrow">→</span>}
            </Fragment>
          ))}
        </div>

        <div className="section-divider" style={{ padding: 0 }}>// SECURITY POSTURE</div>
        <div className="tech-grid">
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// IDENTITY</span></div>
            <div className="kv-inline">
              Sign-in via Google/Microsoft OIDC, verified server-side and keyed to the provider's
              <b> immutable id</b> — never a mutable email.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// AUTHORIZATION</span></div>
            <div className="kv-inline">
              Database-backed roles &amp; permissions, read on <b>every request</b>. Privilege
              amplification and last-admin lockout are guarded.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head"><span>// SESSIONS</span></div>
            <div className="kv-inline">
              App-issued JWT in an HttpOnly, SameSite cookie. <b>Kick</b> and deactivation revoke
              existing sessions on the next request.
            </div>
          </div>
        </div>

        <footer className="landing-footer" style={{ marginTop: 40, border: "none" }}>
          <span>KINGFISHER // ACCESS CONTROL</span>
          <span className="spacer" />
          <Link to={user ? "/dashboard" : "/login"}>Enter console →</Link>
        </footer>
      </div>
    </div>
  );
}
