import { Link } from "react-router-dom";

import { useAuth } from "../auth/AuthContext";
import { MatrixRain } from "../components/MatrixRain";

// Each step: [title, detail, tag]. Tags label the concrete guarantee.
const SIGN_IN: [string, string, string][] = [
  [
    "Client gets a provider ID token",
    "The browser signs in with Google or Microsoft (OIDC) and receives a signed ID token. The app never sees a password.",
    "OIDC",
  ],
  [
    "Token is POSTed to the API",
    "Only over the login route, which a middleware guards to JSON-only Content-Type — a cross-site <form> POST is refused before the body is even parsed.",
    "CSRF-GUARDED",
  ],
  [
    "Signature is verified",
    "Google: against Google's public certs. Microsoft: against the JWKS signing keys. The algorithm is pinned to RS256 — a 'none' or HS-swap token is rejected, not trusted.",
    "SIGNATURE",
  ],
  [
    "Claims are checked",
    "audience == our client id · issuer (Google allowlist / Microsoft bound to the token's own tenant: iss must equal …/{tid}/v2.0) · not expired · email_verified (Google).",
    "AUD · ISS · EXP",
  ],
  [
    "Token is single-use",
    "The exact token is remembered until it expires; replaying the same captured token within its window is rejected.",
    "ANTI-REPLAY",
  ],
  [
    "Identity resolved by immutable id",
    "The account is keyed on the provider's immutable subject — Google `sub` / Microsoft `oid` — never the email. Two providers with one email are two accounts; a changed email can't hijack.",
    "IMMUTABLE SUBJECT",
  ],
  [
    "The app mints its own session",
    "A short-lived app JWT (HS256) with issuer, audience, token type, subject, jti, expiry, and a rotation key id is set in an HttpOnly, SameSite=Lax, __Host- Secure production cookie. The provider token is discarded and never stored.",
    "SESSION COOKIE",
  ],
];

const EVERY_REQUEST: [string, string, string][] = [
  [
    "Decode the session cookie",
    "The app JWT is verified with the pinned HS256 algorithm and configured rotation keyring; iss, aud, type, exp, iat, sub, and jti are required. No cookie or a bad one → 401.",
    "VERIFY",
  ],
  [
    "Load the user fresh from the DB",
    "Role, permissions, and active/approved status are read from the database on every request — never trusted from stale token claims.",
    "DB READ",
  ],
  [
    "Apply the lifecycle + kill-switch gate",
    "Deactivated or unapproved accounts fail closed. Logout revokes that device's persisted session row; kick/all-device actions also advance the account epoch. Revocation is enforced on the next request.",
    "REVOCABLE",
  ],
  [
    "Check the required permission",
    "require_permission(\"users.manage\") checks the freshly-read union of role and direct grants. Missing → 403. The check is data, not a stale token claim.",
    "AUTHORIZE",
  ],
];

const RBAC: [string, string][] = [
  ["Model", "Each user has one role plus optional direct grants; effective permissions are their union and live in the database."],
  ["Runtime editable", "Administrators and authorized roles.manage delegates can maintain the RBAC catalog; changes are enforced on the next request."],
  ["No delegate amplification", "A non-admin delegate can grant only permissions it already holds and cannot mutate a principal whose effective access outranks it. Administrators retain full catalog authority."],
  ["Protected floor", "System roles can't be rewritten, the admin role is locked, and the last active admin can't be demoted or deactivated."],
];

const DEFENSE = [
  "Single-use provider tokens",
  "Durable kick / logout revocation",
  "Login-CSRF guard + SameSite cookies",
  "Location (GPS) visible only to self or managers",
  "CSV export neutralized against formula injection",
  "Adversarially reviewed — caught real escalation bugs",
];

function Steps({ steps }: { steps: [string, string, string][] }) {
  return (
    <ol className="walk">
      {steps.map(([title, detail, tag], i) => (
        <li className="walk-step" key={title}>
          <div className="walk-num">{String(i + 1).padStart(2, "0")}</div>
          <div className="walk-body">
            <div className="walk-head">
              <span className="walk-title">{title}</span>
              <span className="walk-tag">{tag}</span>
            </div>
            <p className="walk-detail">{detail}</p>
          </div>
        </li>
      ))}
    </ol>
  );
}

export function SecurityPage() {
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
        <Link to="/technology">Technology</Link>
        <Link to="/about">About</Link>
        <Link to={user ? "/dashboard" : "/login"}>{user ? "Console" : "Sign in"}</Link>
      </nav>

      <div className="tech-wrap">
        <header className="tech-hero">
          <div className="hero-eyebrow cursor">SYS://security</div>
          <h1>
            Access, <span className="lit">proven every request.</span>
          </h1>
          <p>
            Two questions decide everything: <b>who are you</b> (verified once, at sign-in) and{" "}
            <b>what may you do</b> (checked fresh, on every request). Here's the exact path a
            request takes through both — the same code that runs this console.
          </p>
        </header>

        <div className="section-divider" style={{ padding: 0 }}>
          // SIGN-IN · VERIFYING THE HUMAN
        </div>
        <Steps steps={SIGN_IN} />

        <div className="section-divider" style={{ padding: 0 }}>
          // EVERY REQUEST · THE PERMISSION GATE
        </div>
        <Steps steps={EVERY_REQUEST} />

        <div className="section-divider" style={{ padding: 0 }}>
          // THE RBAC MODEL
        </div>
        <div className="tech-grid">
          {RBAC.map(([head, body]) => (
            <div className="panel panel--brackets tech-card" key={head}>
              <div className="tc-head">
                <span>// {head.toUpperCase()}</span>
              </div>
              <div className="kv-inline">{body}</div>
            </div>
          ))}
        </div>

        <div className="section-divider" style={{ padding: 0 }}>
          // DEFENSE IN DEPTH
        </div>
        <div className="chips">
          {DEFENSE.map((d) => (
            <span className="chip" key={d}>
              ✓ {d}
            </span>
          ))}
        </div>

        <div className="walk-note">
          <b>Provider nonce binding is active:</b> each Google or Microsoft sign-in starts with a
          server-issued, single-use nonce that the provider echoes in its signed token. The request
          path itself is on the{" "}
          <Link to="/technology">Technology page</Link>.
        </div>

        <footer className="landing-footer" style={{ marginTop: 40, border: "none" }}>
          <span>KINGFISHER // SECURITY</span>
          <span className="spacer" />
          <Link to={user ? "/dashboard" : "/login"}>Enter console →</Link>
        </footer>
      </div>
    </div>
  );
}
