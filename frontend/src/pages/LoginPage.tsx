import { useEffect, useRef, useState } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { microsoftLogin } from "../auth/msal";
import type { AuthConfig, Role, User } from "../types";

export function LoginPage() {
  const { user, setUser } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? "/dashboard";

  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [devEmail, setDevEmail] = useState("");
  const [devRole, setDevRole] = useState<Role>("user");
  const googleBtnRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    api.get<AuthConfig>("/api/auth/config").then(setConfig).catch(() => setConfig(null));
  }, []);

  const onSuccess = (u: User) => {
    setUser(u);
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

  // Google Identity Services button
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
          theme: "outline",
          size: "large",
          width: 280,
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
      const idToken = await microsoftLogin();
      await submitToken("/api/auth/microsoft", idToken);
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
    <div className="login-hero">
      <div className="login-card">
        <span className="logo lg">KF</span>
        <h1>Kingfisher CRM</h1>
        <p className="muted">Sign in with your work account</p>

        {error && <p className="error">{error}</p>}

        {config?.google && <div className="google-btn" ref={googleBtnRef} />}

        {config?.microsoft && (
          <button className="id-btn microsoft" onClick={handleMicrosoft}>
            <span className="ms-glyph" aria-hidden="true">
              <i /><i /><i /><i />
            </span>
            Sign in with Microsoft
          </button>
        )}

        {config?.dev && (
          <form className="dev-login" onSubmit={handleDev}>
            <div className="dev-tag">Dev login (local only)</div>
            <input
              type="email"
              required
              placeholder="you@example.com"
              value={devEmail}
              onChange={(e) => setDevEmail(e.target.value)}
            />
            <select value={devRole} onChange={(e) => setDevRole(e.target.value as Role)}>
              <option value="user">user</option>
              <option value="admin">admin</option>
            </select>
            <button type="submit" className="id-btn primary">
              Dev sign in
            </button>
          </form>
        )}

        {noProviders && (
          <p className="muted small">
            No sign-in methods are configured yet. Add Google/Microsoft client IDs to
            <code> .env</code>, or set <code>DEV_AUTH_ENABLED=true</code> for local dev.
          </p>
        )}
      </div>
    </div>
  );
}
