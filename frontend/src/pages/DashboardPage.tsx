import { useEffect, useState } from "react";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { OnlineDot, RoleBadge } from "../components/badges";
import { Panel } from "../components/Panel";
import { usePresence } from "../presence/PresenceContext";
import type { DashboardData } from "../types";

const SPARK = ["▂▃▅▂▇▅▆▃", "▁▄▂▅▃▆▇▅", "▃▅▆▇▆▅▃▂"];

export function DashboardPage() {
  const { user } = useAuth();
  const presence = usePresence();
  const [data, setData] = useState<DashboardData | null>(null);

  useEffect(() => {
    api.get<DashboardData>("/api/dashboard").then(setData).catch(() => setData(null));
  }, []);

  const onlineNow = presence.canView ? presence.count : (data?.stats.online_users ?? "—");

  return (
    <div>
      <h1 className="page-title">
        SYS://dashboard <RoleBadge role={user?.role ?? ""} />
      </h1>
      <p className="page-sub">
        Operator {user?.display_name ?? user?.email} · session secure ·{" "}
        {data?.message ?? "syncing…"}
      </p>

      <div className="kpi-grid">
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Total users</div>
          <div className="kpi-value">{data?.stats.total_users ?? "—"}</div>
          <div className="sparkline">{SPARK[0]}</div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Active users</div>
          <div className="kpi-value">{data?.stats.active_users ?? "—"}</div>
          <div className="sparkline">{SPARK[1]}</div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Online now</div>
          <div className="kpi-value">
            {onlineNow}
            <span className="unit">live</span>
          </div>
          <div className="sparkline">{SPARK[2]}</div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Your access</div>
          <div className="kpi-value" style={{ fontSize: 18 }}>
            {user?.permissions.length ?? 0} perms
          </div>
          <div className="sparkline muted" style={{ letterSpacing: 0 }}>
            {user?.role}
          </div>
        </div>
      </div>

      <div className="grid-2">
        <Panel
          title="// ONLINE NOW"
          right={<span className="status-dot" />}
        >
          {!presence.canView ? (
            <div className="online-empty">Requires the presence.view permission.</div>
          ) : presence.online.length === 0 ? (
            <div className="online-empty">No one else is online right now.</div>
          ) : (
            <div className="online-list">
              {presence.online.map((u) => (
                <div className="online-row" key={u.id}>
                  <OnlineDot online />
                  <span className="who">{u.display_name ?? u.email}</span>
                  <RoleBadge role={u.role} />
                </div>
              ))}
            </div>
          )}
        </Panel>

        <Panel title="// SYSTEM">
          <p className="sans dim" style={{ marginTop: 0 }}>
            This dashboard is the v1 placeholder. Identity, roles &amp; permissions, presence,
            and the session kill-switch are live — real CRM features slot in from here.
          </p>
          <div className="placeholder" style={{ marginTop: 12 }}>
            More instruments coming online soon.
          </div>
        </Panel>
      </div>
    </div>
  );
}
