import {
  createContext,
  type Dispatch,
  type ReactNode,
  type SetStateAction,
  useContext,
  useEffect,
  useState,
} from "react";

import { api, setUnauthorizedHandler } from "../api/client";
import type { AuthConfig, User } from "../types";
import { applyAccent, applyCalm, applyUiPrefs } from "../util/theme";

interface AuthContextValue {
  user: User | null;
  loading: boolean;
  setUser: Dispatch<SetStateAction<User | null>>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const authenticatedUserId = user?.id;

  useEffect(() => {
    // A mid-session 401 clears auth state; ProtectedRoute then redirects to /login.
    setUnauthorizedHandler(() => setUser(null));
    // Global calm mode is an admin site setting, surfaced on the public config.
    api
      .get<AuthConfig>("/api/auth/config")
      .then((c) => applyCalm(c.calm))
      .catch(() => {});
    api
      .get<User>("/api/auth/me", { suppressAuthRedirect: true })
      .then(setUser)
      .catch(() => setUser(null))
      .finally(() => setLoading(false));
    return () => setUnauthorizedHandler(null);
  }, []);

  // Apply the signed-in user's accent color (reverts to default when signed out).
  useEffect(() => {
    applyAccent(user?.accent_color);
  }, [user?.accent_color]);

  // Apply the signed-in user's UI preference bag (reverts to defaults when signed out).
  useEffect(() => {
    applyUiPrefs(user?.ui_prefs ?? null);
  }, [user?.ui_prefs]);

  // Keep the session fresh: re-fetch the user on window focus and every few minutes, so a
  // permission change (role edit, direct grant, deactivation) is reflected without a manual
  // reload. A 401 here trips the shared unauthorized handler, which signs the user out.
  useEffect(() => {
    if (!authenticatedUserId) return;
    const refresh = () => {
      api
        .get<User>("/api/auth/me")
        .then(setUser)
        .catch(() => {});
    };
    window.addEventListener("focus", refresh);
    const timer = setInterval(refresh, 5 * 60 * 1000);
    return () => {
      window.removeEventListener("focus", refresh);
      clearInterval(timer);
    };
  }, [authenticatedUserId]);

  const logout = async () => {
    await api.post("/api/auth/logout");
    setUser(null);
  };

  return (
    <AuthContext.Provider value={{ user, loading, setUser, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside AuthProvider");
  return ctx;
}
