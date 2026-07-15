import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

import { api, setUnauthorizedHandler } from "../api/client";
import type { AuthConfig, User } from "../types";
import { applyAccent, applyCalm } from "../util/theme";

interface AuthContextValue {
  user: User | null;
  loading: boolean;
  setUser: (u: User | null) => void;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

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
