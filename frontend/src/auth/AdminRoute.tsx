import { Navigate, Outlet } from "react-router-dom";

import { useAuth } from "./AuthContext";

export function AdminRoute() {
  const { user } = useAuth(); // ProtectedRoute already guaranteed a user
  if (user!.role !== "admin") return <Navigate to="/dashboard" replace />;
  return <Outlet />;
}
