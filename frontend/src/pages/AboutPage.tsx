import { Link } from "react-router-dom";

import { MatrixRain } from "../components/MatrixRain";

const STATS: [string, string][] = [
  ["Role", "Founder / Engineer"],
  ["Background", "U.S. Army veteran"],
  ["Focus", "Agentic AI"],
  ["Building AI", "3+ years"],
  ["Mode", "Solo + a team of agents"],
];

const SKILLS = [
  "C#/.NET", "TypeScript", "Python", "SQL", "Angular", "RxJS", "PostgreSQL",
  "Docker", "AWS", "CI/CD · OIDC", "Networking", "Security",
];

const CERTS = ["CISSP", "AWS SA", "CCNP", "RHCSA", "Security+", "Network+"];

export function AboutPage() {
  return (
    <div className="about">
      <MatrixRain opacity={0.06} />
      <div className="scanlines" />

      <nav className="landing-nav">
        <Link to="/" className="brand">
          ◈ KINGFISHER
        </Link>
        <span className="spacer" />
        <Link to="/">Home</Link>
        <Link to="/technology">Technology</Link>
        <Link to="/security">Security</Link>
        <Link to="/login">Sign in</Link>
      </nav>

      <div className="dossier">
        <aside className="panel panel--brackets id-card">
          <div className="id-avatar">JF</div>
          <div className="id-name">Junior Fortunato</div>
          <div className="id-role">// Founder · Engineer · Operator</div>
          <div className="stat-block">
            {STATS.map(([k, v]) => (
              <div className="stat-row" key={k}>
                <span className="k">{k}</span>
                <span className="v">{v}</span>
              </div>
            ))}
          </div>
          <div className="contact-rail">
            <a className="btn btn-sm btn-ghost" href="https://usageiq.online" target="_blank" rel="noreferrer">
              usageiq.online ↗
            </a>
          </div>
        </aside>

        <div>
          <h1 className="page-title" style={{ fontSize: 26 }}>
            The builder behind the console<span className="cursor" />
          </h1>

          <div className="bio-section-label">// OPERATOR</div>
          <div className="bio">
            <p>
              <strong>Junior Fortunato</strong> is a founder, U.S. Army veteran, and full-stack
              engineer who ships production systems solo — designing, building, deploying, and
              operating end to end across <strong>C#/.NET, Angular, TypeScript, Python, and SQL</strong>.
              One person at the keyboard, a team of agents on the grind, and live software to show
              for it.
            </p>
          </div>

          <div className="pullquote">One person, a team of agents.</div>

          <div className="bio-section-label">// FOCUS · AGENTIC AI</div>
          <div className="bio">
            <p>
              Three-plus years deep into agentic AI — among the first to build seriously with
              OpenAI Codex and Claude — he architects multi-agent development pipelines where the
              models handle review, refactoring, testing, and documentation while he steers intent.
              Sharp prompt engineering turns a fleet of agents into a force multiplier, and the
              proof is shipping software, not slideware.
            </p>
          </div>

          <div className="bio-section-label">// FOUNDER</div>
          <div className="bio">
            <p>
              He built <strong>Horizon Authentication</strong> single-handedly — a secure
              point-of-sale, CRM, and staff-management platform delivered across its full
              lifecycle on an AWS CI/CD pipeline, as sole engineer for full-stack, infrastructure,
              security, and uptime. A distinctive API-proxy feature let customers route their data
              securely through the platform — a real edge.
            </p>
          </div>

          <div className="bio-section-label">// SIGNAL · VETERAN</div>
          <div className="bio">
            <p>
              As a <strong>Senior Wideband Controller</strong>, Junior led communications for
              high-stakes missions — drone operations, maritime navigation, and submarine comms —
              owning the RF chain from modem to satellite. He became the unit's youngest to hold
              the role and trained the controllers who came after. Discipline and reliability under
              pressure carry into every system he ships.
            </p>
          </div>

          <div className="pullquote">The best résumé is a running product.</div>

          <div className="bio-section-label">// TOOLKIT</div>
          <div className="chips">
            {SKILLS.map((s) => (
              <span className="chip" key={s}>
                {s}
              </span>
            ))}
          </div>

          <div className="bio-section-label">// CERTIFICATIONS</div>
          <div className="chips">
            {CERTS.map((c) => (
              <span className="chip" key={c}>
                ✓ {c}
              </span>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
