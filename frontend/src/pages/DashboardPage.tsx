import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { RoleBadge } from "../components/RoleBadge";
import type { DashboardData } from "../types";

export function DashboardPage() {
  const { user } = useAuth();
  const [data, setData] = useState<DashboardData | null>(null);

  useEffect(() => {
    api.get<DashboardData>("/api/dashboard").then(setData).catch(() => setData(null));
  }, []);

  return (
    <div className="page">
      <h1 className="page-title">
        Welcome, {user?.display_name ?? user?.email} {user && <RoleBadge role={user.role} />}
      </h1>
      <p className="muted">{data?.message ?? "Loading…"}</p>

      <div className="tiles">
        <div className="tile">
          <div className="tile-label">Total users</div>
          <div className="tile-value">{data?.stats.total_users ?? "—"}</div>
        </div>
        <div className="tile">
          <div className="tile-label">Active users</div>
          <div className="tile-value">{data?.stats.active_users ?? "—"}</div>
        </div>
        {user?.role === "admin" && (
          <Link className="tile tile-link" to="/admin/users">
            <div className="tile-label">Manage</div>
            <div className="tile-value sm">User management →</div>
          </Link>
        )}
      </div>

      <div className="placeholder">More coming soon — this dashboard is a v1 placeholder.</div>
    </div>
  );
}
