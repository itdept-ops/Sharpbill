import { useEffect, useRef, useState } from "react";
import { Link, Navigate, useLocation, useNavigate } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
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

  const onSuccess = (u: User) => {
    setUser(u);
    captureLocation(); // optional: prompts for GPS, silently ignored if denied
    navigate(from, { replace: true });
  };

  useEffect(() => {
    if (!config?.google) return;
    let tries = 0;
    const timer = setInterval(() => {
      if (window.google && googleBtnRef.current) {
        clearInterval(timer);
        window.google.accounts.id.initialize({
          client_id: import.meta.env.VITE_GOOGLE_CLIENT_ID,
          callback: async (resp) => {
            setError(null);
            try {
              onSuccess(await api.post<User>("/api/auth/google", { id_token: resp.credential }));
            } catch (e) {
              setError(e instanceof ApiError ? e.message : "Sign-in failed");
            }
          },
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

  if (user) return <Navigate to={from} replace />;

  return (
    <div className="auth-wrap">
      <MatrixRain opacity={0.24} />
      <div className="scanlines" />

      <section className="panel panel--brackets auth-panel">
        <div className="panel-header">
          <span>
            // AUTHENTICATE<span className="cursor" />
          </span>
        </div>
        <div className="auth-body">
          <div className="auth-brand">
            <span className="logo-glyph" style={{ color: "var(--green)", fontSize: 22 }}>
              ◈
            </span>
            <strong style={{ letterSpacing: "0.06em" }}>KINGFISHER</strong>
          </div>

          <p className="muted small" style={{ margin: 0 }}>
            Sign in with your Google account to continue.
          </p>

          {error && <div className="auth-error">ERR: {error}</div>}

          {config?.google && <div className="google-slot" ref={googleBtnRef} />}

          {config && !config.google && !configFailed && (
            <p className="muted" style={{ fontSize: 12 }}>
              Google sign-in isn't configured yet — set <code>GOOGLE_CLIENT_ID</code> to enable it.
            </p>
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

          <div className="auth-links">
            <Link to="/">← Back to site</Link>
            <Link to="/security">How access works →</Link>
          </div>
        </div>
      </section>
    </div>
  );
}
