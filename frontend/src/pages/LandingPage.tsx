import { Link } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { LegalNav } from "../components/LegalNav";
import { MatrixRain } from "../components/MatrixRain";

const FEATURES = [
  ["// IDENTITY", "When configured and enabled, Google and Microsoft tokens are verified server-side and keyed to the provider's immutable id — not an email that can be changed."],
  ["// ROLES & ACCESS", "A real permission system. Define roles, mint new permissions, and assign them — enforced from the database on every request."],
  ["// LIVE PRESENCE", "See who is online in real time. Presence rides the session, so the roster reflects who is actually at the console."],
  ["// KILL-SWITCH", "Kick any user in one click. Their session is revoked on the next request — no waiting for a token to expire."],
  ["// LOCAL STACK", "API, database, and web run together through loopback-only Docker Compose. Production infrastructure and recovery controls are separate."],
  ["// REQUEST TRACE", "Permission-gated request activity supports investigation. Production audit evidence still requires restricted, append-only export."],
];

export function LandingPage() {
  const { user } = useAuth();
  const enter = user ? "/dashboard" : "/login";

  return (
    <div className="landing">
      <MatrixRain opacity={0.4} />
      <div className="scanlines" />

      <nav className="landing-nav">
        <span className="brand">◈ SHARPBILL</span>
        <span className="spacer" />
        <Link to="/technology">Technology</Link>
        <Link to="/security">Security</Link>
        <Link to="/about">About</Link>
        <Link to={enter}>{user ? "Console" : "Sign in"}</Link>
      </nav>

      <section className="hero">
        <div>
          <div className="hero-eyebrow cursor">SHARPBILL // ACCESS CONTROL CONSOLE</div>
          <h1 className="hero-title">
            Access, <span className="lit">proven every request.</span>
          </h1>
          <p className="hero-sub">
            An access-control console: single sign-on verified server-side and keyed to a
            provider's immutable identity, database-backed roles and permissions enforced on every
            request, live presence, and a one-click session kill-switch — built to be run, not just
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
            <span className="status-dot" /> IDENTITY <b>IMMUTABLE</b>
          </div>
          <div className="readout">
            <span className="status-dot" /> AUTH <b>EVERY REQUEST</b>
          </div>
          <div className="readout">
            <span className="status-dot teal" /> SESSIONS <b>REVOCABLE</b>
          </div>
          <div className="readout">
            <span className="status-dot" /> REVIEW <b>ADVERSARIAL</b>
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
        <span>SHARPBILL // ACCESS CONTROL</span>
        <span className="spacer" />
        <LegalNav className="footer-legal-nav" />
      </footer>
    </div>
  );
}
