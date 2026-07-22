import { Fragment } from "react";
import { Link } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { MatrixRain } from "../components/MatrixRain";

const STACK = [
  {
    head: "// FRONTEND",
    badge: "SPA",
    items: [
      "React 18 + TypeScript",
      "Vite build and dev server",
      "React Router v6",
      "Vitest + Playwright coverage",
      "Bespoke CSS with accessible controls",
    ],
  },
  {
    head: "// BACKEND",
    badge: "API",
    items: [
      ".NET 10 + ASP.NET Core",
      "Clean service interfaces and dependency injection",
      "Middleware for context, CSRF, logging, limits, and errors",
      "Dapper + MySqlConnector",
      "Bounded MySQL transient retry handling",
      "Worker services for retention and log flushing",
    ],
  },
  {
    head: "// DATA",
    badge: "RDBMS",
    items: [
      "MySQL 8.4 LTS + utf8mb4",
      "Roles, permissions, identities, sessions, and legal acceptance",
      "Per-request authorization reads",
      "Cursor-paged request logs",
      "Journaled C# schema migrator at baseline 0021",
    ],
  },
  {
    head: "// PLATFORM",
    badge: "DEVOPS",
    items: [
      "Loopback-only Docker Compose for local operation",
      "Digest-pinned runtime images",
      "Non-root production web image",
      "GitHub Actions quality, E2E, image, SBOM, and Trivy checks",
      "Caddy production topology documented separately",
    ],
  },
];

const FLOW = [
  ["Browser", "React SPA, cookie-only session"],
  ["Edge", "Vite proxy locally / Caddy reference for production"],
  ["API", "ASP.NET Core verifies identity, authorizes, and serves"],
  ["Data", "MySQL 8.4 LTS with roles read every request"],
];

export function TechnologyPage() {
  const { user } = useAuth();
  return (
    <div className="tech">
      <MatrixRain opacity={0.14} />
      <div className="scanlines" />

      <nav className="landing-nav">
        <Link to="/" className="brand">
          &#9672; SHARPBILL
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
            Built the modern way: <span className="lit">reviewed, typed, containerized.</span>
          </h1>
          <p>
            Sharpbill is a full-stack access-control console with a React front end, an
            enterprise-style C# backend, MySQL persistence, explicit migrations, Docker-first local
            runtime, and repository gates for quality and supply-chain evidence. This page describes
            the current repository baseline, not a production infrastructure claim.
          </p>
        </header>

        <div className="section-divider" style={{ padding: 0 }}>
          // THE STACK
        </div>
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

        <div className="section-divider" style={{ padding: 0 }}>
          // ENGINEERING MODEL
        </div>
        <div className="tech-grid">
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// APPLICATION</span>
              <span className="badge">CLEAN</span>
            </div>
            <div className="kv-inline">
              The API is split into contracts, domain, application abstractions, infrastructure,
              workers, and the web host. Controllers stay thin while services own behavior and
              repositories own persistence.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// DELIVERY</span>
              <span className="badge">CI</span>
            </div>
            <ul>
              <li>Frontend quality and unit tests</li>
              <li>Backend quality, architecture, and integration tests</li>
              <li>End-to-end access-control checks</li>
              <li>Production image and supply-chain verification</li>
            </ul>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// PROOF</span>
              <span className="badge">EVIDENCE</span>
            </div>
            <div className="kv-inline">
              Security fixes are captured in tests, the backend audit report, and small commits on
              <b> main</b>. The repo is private, branch-protected, and backed by passing Actions
              checks.
            </div>
          </div>
        </div>

        <div className="section-divider" style={{ padding: 0 }}>
          // REQUEST PATH
        </div>
        <div className="flow">
          {FLOW.map(([n, d], i) => (
            <Fragment key={n}>
              <div className="flow-node">
                <div className="fn">{n}</div>
                <div className="fd">{d}</div>
              </div>
              {i < FLOW.length - 1 && <span className="flow-arrow">&rarr;</span>}
            </Fragment>
          ))}
        </div>

        <div className="section-divider" style={{ padding: 0 }}>
          // SECURITY POSTURE
        </div>
        <div className="tech-grid">
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// IDENTITY</span>
            </div>
            <div className="kv-inline">
              When configured and enabled, Google/Microsoft OIDC is verified server-side and keyed
              to the provider's <b>immutable id</b>, never a mutable email. Login nonces are stored
              and admitted without a single hot global lock.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// AUTHORIZATION</span>
            </div>
            <div className="kv-inline">
              Database-backed roles and permissions are read on <b>every request</b>. Privilege
              amplification, last-admin lockout, and disabled-user session paths are guarded.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// SESSIONS</span>
            </div>
            <div className="kv-inline">
              App-issued JWT in an HttpOnly, SameSite cookie. <b>Kick</b> and deactivation revoke
              existing sessions on the next request.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// OBSERVABILITY</span>
            </div>
            <div className="kv-inline">
              Request IDs are server-owned, request-log loss is counted, retention health is
              exposed, and CSV exports are bounded and streamed.
            </div>
          </div>
          <div className="panel panel--brackets tech-card">
            <div className="tc-head">
              <span>// BOUNDARY</span>
            </div>
            <div className="kv-inline">
              AWS, backups, centralized SIEM/WORM storage, and production rate-limit topology are
              intentionally production-owned controls, outside the local landing-page claim.
            </div>
          </div>
        </div>

        <footer className="landing-footer" style={{ marginTop: 40, border: "none" }}>
          <span>SHARPBILL // ACCESS CONTROL</span>
          <span className="spacer" />
          <Link to={user ? "/dashboard" : "/login"}>Enter console &rarr;</Link>
        </footer>
      </div>
    </div>
  );
}
