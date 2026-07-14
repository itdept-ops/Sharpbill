import { useEffect, useState } from "react";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { OnlineDot, RoleBadge } from "../components/badges";
import { AreaChart, BarChart, Donut, SegmentBar, SERIES } from "../components/Charts";
import { Panel } from "../components/Panel";
import { usePresence } from "../presence/PresenceContext";
import type { Analytics, DashboardData } from "../types";

const md = (iso: string) => iso.slice(5).replace("-", "/");

export function DashboardPage() {
  const { user } = useAuth();
  const presence = usePresence();
  const [data, setData] = useState<DashboardData | null>(null);
  const [an, setAn] = useState<Analytics | null>(null);

  const canAnalytics = !!user?.permissions.includes("users.read");

  useEffect(() => {
    api.get<DashboardData>("/api/dashboard").then(setData).catch(() => setData(null));
    if (canAnalytics) api.get<Analytics>("/api/dashboard/analytics").then(setAn).catch(() => setAn(null));
  }, [canAnalytics]);

  const onlineNow = presence.canView ? presence.count : (data?.stats.online_users ?? "—");

  return (
    <div>
      <h1 className="page-title">
        SYS://dashboard <RoleBadge role={user?.role ?? ""} />
      </h1>
      <p className="page-sub">
        Operator {user?.display_name ?? user?.email} · session secure · directory &amp; access control
      </p>

      <div className="kpi-grid">
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Total users</div>
          <div className="kpi-value">{data?.stats.total_users ?? "—"}</div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Active</div>
          <div className="kpi-value">{data?.stats.active_users ?? "—"}</div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Online now</div>
          <div className="kpi-value">{onlineNow}<span className="unit">live</span></div>
        </div>
        <div className="panel panel--brackets kpi">
          <div className="kpi-label">Awaiting approval</div>
          <div className="kpi-value">{an ? an.status.pending : "—"}</div>
        </div>
      </div>

      {canAnalytics && an && (
        <>
          <div className="grid-2" style={{ marginBottom: 16 }}>
            <Panel title="// SIGN-UPS · LAST 14 DAYS">
              <AreaChart points={an.signups.map((s) => ({ label: md(s.date), value: s.count }))} />
            </Panel>
            <Panel title="// ACCOUNT STATUS">
              <SegmentBar
                segments={[
                  { label: "active", value: an.status.active, color: SERIES[0], glyph: "●" },
                  { label: "pending", value: an.status.pending, color: SERIES[2], glyph: "◆" },
                  { label: "disabled", value: an.status.disabled, color: SERIES[4], glyph: "✕" },
                ]}
              />
              <div style={{ marginTop: 14 }} className="kpi-label">Online right now</div>
              <div className="kpi-value" style={{ fontSize: 22 }}>{an.status.online}</div>
            </Panel>
          </div>

          <div className="grid-2" style={{ marginBottom: 16 }}>
            <Panel title="// USERS BY ROLE">
              <BarChart data={an.roles.map((r) => ({ label: r.role, value: r.count }))} />
            </Panel>
            <Panel title="// SIGN-IN PROVIDERS">
              <Donut
                caption="ACCOUNTS"
                segments={an.providers.map((p, i) => ({ label: p.provider, value: p.count, color: SERIES[i % SERIES.length] }))}
              />
            </Panel>
          </div>
        </>
      )}

      <div className="grid-2">
        <Panel
          title="// ONLINE NOW"
          right={
            <span className="online-count" style={{ fontSize: 10 }}>
              {presence.live ? "● live" : "polling"}
            </span>
          }
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
                  <span className="who">{u.display_name ?? "member"}</span>
                  <RoleBadge role={u.role} />
                </div>
              ))}
            </div>
          )}
        </Panel>
        <Panel title="// YOUR ACCESS">
          <div className="kv-row" style={{ marginBottom: 10 }}>
            <span className="k">Role</span>
            <span className="v"><RoleBadge role={user?.role ?? ""} /></span>
          </div>
          <div className="kpi-label">Permissions ({user?.permissions.length ?? 0})</div>
          <div className="perm-chips" style={{ marginTop: 8 }}>
            {(user?.permissions ?? []).map((p) => (
              <span key={p} className="perm-chip">{p}</span>
            ))}
          </div>
        </Panel>
      </div>
    </div>
  );
}
