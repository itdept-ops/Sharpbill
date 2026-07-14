import type { ReactNode } from "react";
import { Navigate } from "react-router-dom";

import { useAuth } from "./AuthContext";

/** Renders children only if the current user holds `perm`; otherwise redirects. */
export function RequirePermission({ perm, children }: { perm: string; children: ReactNode }) {
  const { user } = useAuth();
  if (!user || !user.permissions.includes(perm)) {
    return <Navigate to="/dashboard" replace />;
  }
  return <>{children}</>;
}
