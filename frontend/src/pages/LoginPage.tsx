import { useEffect, useRef, useState } from "react";
import { Link, Navigate, useLocation, useNavigate } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { microsoftLogin } from "../auth/msal";
import { MatrixRain } from "../components/MatrixRain";
import type { AuthConfig, User } from "../types";
import { captureLocation } from "../util/location";

export function LoginPage() {
  const { user, setUser } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? "/dashboard";

  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [configFailed, setConfigFailed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [devEmail, setDevEmail] = useState("");
  const [devRole, setDevRole] = useState("user");
  const [devRoles, setDevRoles] = useState<string[]>(["user", "admin"]);
  const googleBtnRef = useRef<HTMLDivElement>(null);

  const loadConfig = () => {
    setConfigFailed(false);
    api
      .get<AuthConfig>("/api/auth/config")
      .then(setConfig)
      .catch(() => {
        setConfig(null);
        setConfigFailed(true);
      });
  };

  useEffect(loadConfig, []);

  // Populate the dev role dropdown with every role (system + custom) when dev login is available.
  useEffect(() => {
    if (!config?.dev) return;
    api
      .get<string[]>("/api/auth/dev/roles")
      .then((roles) => {
        if (roles.length) setDevRoles(roles);
      })
      .catch(() => {
        /* keep the user/admin fallback */
      });
  }, [config?.dev]);

  const onSuccess = (u: User) => {
    setUser(u);
    captureLocation(); // optional: prompts for GPS, silently ignored if denied
    navigate(from, { replace: true });
  };

  const submitToken = async (path: string, idToken: string) => {
    setError(null);
    try {
      onSuccess(await api.post<User>(path, { id_token: idToken }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Sign-in failed");
    }
  };

  useEffect(() => {
    if (!config?.google) return;
    let tries = 0;
    const timer = setInterval(() => {
      if (window.google && googleBtnRef.current) {
        clearInterval(timer);
        window.google.accounts.id.initialize({
          client_id: import.meta.env.VITE_GOOGLE_CLIENT_ID,
          callback: (resp) => submitToken("/api/auth/google", resp.credential),
        });
        window.google.accounts.id.renderButton(googleBtnRef.current, {
          theme: "filled_black",
          size: "large",
          width: 320,
        });
      } else if (++tries > 50) {
        clearInterval(timer);
      }
    }, 100);
    return () => clearInterval(timer);
  }, [config]);

  const handleMicrosoft = async () => {
    setError(null);
    try {
      await submitToken("/api/auth/microsoft", await microsoftLogin());
    } catch {
      setError("Microsoft sign-in was cancelled or failed");
    }
  };

  const handleDev = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      onSuccess(await api.post<User>("/api/auth/dev", { email: devEmail, role: devRole }));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Dev sign-in failed");
    }
  };

  if (user) return <Navigate to={from} replace />;

  const noProviders = config && !config.google && !config.microsoft && !config.dev;

  return (
    <div className="auth-wrap">
      <MatrixRain opacity={0.24} />
      <div className="scanlines" />

      <section className="panel panel--brackets auth-panel">
        <div className="panel-header">
          <span>
            // AUTHENTICATE<span className="cursor" />
          </span>
          {config?.dev && <span className="pill-amber">DEV MODE</span>}
        </div>
        <div className="auth-body">
          <div className="auth-brand">
            <span className="logo-glyph" style={{ color: "var(--green)", fontSize: 22 }}>
              ◈
            </span>
            <strong style={{ letterSpacing: "0.06em" }}>KINGFISHER CRM</strong>
          </div>

          {error && <div className="auth-error">ERR: {error}</div>}

          {config?.google && <div className="google-slot" ref={googleBtnRef} />}

          {config?.microsoft && (
            <button className="sso-btn" onClick={handleMicrosoft}>
              <span className="sso-mark">
                <span className="msq">
                  <i />
                  <i />
                  <i />
                  <i />
                </span>
              </span>
              Continue with Microsoft
            </button>
          )}

          {config?.dev && (config.google || config.microsoft) && (
            <div className="auth-divider">── OR // LOCAL DEV ──</div>
          )}

          {config?.dev && (
            <form className="auth-body" style={{ padding: 0, gap: 10 }} onSubmit={handleDev}>
              <div className="field">
                <label className="field-label">&gt; email</label>
                <input
                  className="field-input"
                  type="email"
                  required
                  placeholder="you@example.com"
                  value={devEmail}
                  onChange={(e) => setDevEmail(e.target.value)}
                />
              </div>
              <div className="field">
                <label className="field-label">&gt; role</label>
                <select
                  className="field-input"
                  value={devRole}
                  onChange={(e) => setDevRole(e.target.value)}
                >
                  {devRoles.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </select>
              </div>
              <button type="submit" className="btn btn-primary" style={{ marginTop: 4 }}>
                Authenticate ▍
              </button>
            </form>
          )}

          {configFailed && (
            <div className="auth-error" style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <span>ERR: could not reach the sign-in service.</span>
              <span className="spacer" />
              <button className="btn btn-ghost btn-sm" onClick={loadConfig}>
                Retry
              </button>
            </div>
          )}

          {noProviders && (
            <p className="muted" style={{ fontSize: 12 }}>
              No sign-in methods are configured. Add Google/Microsoft client IDs, or set{" "}
              <code>DEV_AUTH_ENABLED=true</code> for local dev.
            </p>
          )}

          <div className="auth-links">
            <Link to="/">← Back to site</Link>
            <Link to="/security">How access works →</Link>
          </div>
        </div>
      </section>
    </div>
  );
}
