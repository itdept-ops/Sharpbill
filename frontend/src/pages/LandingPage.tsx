import { Link } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { MatrixRain } from "../components/MatrixRain";

const FEATURES = [
  ["// IDENTITY", "Sign in with Google or Microsoft. Tokens are verified server-side and keyed to the provider's immutable id — not an email that can be changed."],
  ["// ROLES & ACCESS", "A real permission system. Define roles, mint new permissions, and assign them — enforced from the database on every request."],
  ["// LIVE PRESENCE", "See who is online in real time. Presence rides the session, so the roster reflects who is actually at the console."],
  ["// KILL-SWITCH", "Kick any user in one click. Their session is revoked on the next request — no waiting for a token to expire."],
  ["// DOCKERIZED", "The whole stack — API, database, and web — comes up with one command. Hot reload in dev, reproducible everywhere."],
  ["// AUDIT-READY", "Every login records the verified provider id. Deactivate, reassign, or revoke — the trail is in the data."],
];

export function LandingPage() {
  const { user } = useAuth();
  const enter = user ? "/dashboard" : "/login";

  return (
    <div className="landing">
      <MatrixRain opacity={0.4} />
      <div className="scanlines" />

      <nav className="landing-nav">
        <span className="brand">◈ KINGFISHER CRM</span>
        <span className="spacer" />
        <Link to="/about">About</Link>
        <Link to={enter}>{user ? "Console" : "Sign in"}</Link>
      </nav>

      <section className="hero">
        <div>
          <div className="hero-eyebrow cursor">KINGFISHER CRM // OPERATIONS DECK</div>
          <h1 className="hero-title">
            Your pipeline, <span className="lit">on the wire.</span>
          </h1>
          <p className="hero-sub">
            An operator-grade CRM foundation: single sign-on, database-managed roles and
            permissions, live presence, and a session kill-switch — built to be run, not just
            browsed.
          </p>
          <div className="hero-cta">
            <Link className="btn btn-cmd btn-primary" to={enter}>
              [ ENTER CONSOLE ]
            </Link>
            <Link className="btn btn-cmd btn-ghost" to="/about">
              [ ABOUT THE BUILDER ]
            </Link>
          </div>
        </div>
        <div className="status-rail">
          <div className="readout">
            <span className="status-dot" /> UPTIME <b>99.98%</b>
          </div>
          <div className="readout">
            <span className="status-dot" /> NODES <b>12/12</b>
          </div>
          <div className="readout">
            <span className="status-dot teal" /> LATENCY <b>41ms</b>
          </div>
          <div className="readout">
            <span className="status-dot" /> SESSIONS <b>SECURE</b>
          </div>
        </div>
      </section>

      <div className="section-divider">// CAPABILITIES</div>

      <div className="feature-grid">
        {FEATURES.map(([t, d]) => (
          <div key={t} className="panel panel--brackets feature-card">
            <div className="ft">{t}</div>
            <p>{d}</p>
          </div>
        ))}
      </div>

      <footer className="landing-footer">
        <span>KF-CRM v4.0 // BUILD 20260713</span>
        <span className="spacer" />
        <span>Local-first · Dockerized · Open</span>
      </footer>
    </div>
  );
}
