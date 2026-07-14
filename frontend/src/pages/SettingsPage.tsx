import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { RoleBadge } from "../components/badges";
import { Panel } from "../components/Panel";
import type { Role, SignupMode, SiteSettings, User, UserList } from "../types";

const MODES: { v: SignupMode; label: string; desc: string }[] = [
  { v: "open", label: "Open", desc: "Anyone who signs in gets an account immediately." },
  { v: "approval", label: "Approval", desc: "New sign-ins wait for an admin to approve them." },
  { v: "closed", label: "Closed", desc: "No new accounts can be created." },
];

export function SettingsPage() {
  const [s, setS] = useState<SiteSettings | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [pending, setPending] = useState<User[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);

  useEffect(() => {
    api.get<SiteSettings>("/api/admin/settings").then(setS).catch(() => setS(null));
    api.get<Role[]>("/api/roles").then(setRoles).catch(() => setRoles([]));
    api
      .get<UserList>("/api/users?status=pending")
      .then((r) => setPending(r.items))
      .catch(() => setPending([]));
  }, []);

  const update = async (patch: Partial<SiteSettings>) => {
    setBanner(null);
    try {
      setS(await api.put<SiteSettings>("/api/admin/settings", patch));
      setBanner({ msg: "Settings saved.", ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Save failed" });
    }
  };

  const approve = async (u: User) => {
    setBanner(null);
    try {
      await api.post(`/api/users/${u.id}/approve`);
      setPending((p) => p.filter((x) => x.id !== u.id));
      setBanner({ msg: `Approved ${u.email}.`, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Approve failed" });
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://admin / settings</h1>
      <p className="page-sub">Control how people join and which providers are accepted.</p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="settings-grid">
        <Panel title="// ACCESS">
          {!s ? (
            <div className="muted">Loading…</div>
          ) : (
            <>
              <div className="set-row">
                <div className="set-label">
                  <div className="st">Sign-up mode</div>
                  <div className="sd">{MODES.find((m) => m.v === s.signup_mode)?.desc}</div>
                </div>
              </div>
              <div className="mode-picker" style={{ marginBottom: 8 }}>
                {MODES.map((m) => (
                  <button
                    key={m.v}
                    className={`mode-opt ${s.signup_mode === m.v ? "active" : ""}`}
                    onClick={() => update({ signup_mode: m.v })}
                  >
                    {m.label}
                  </button>
                ))}
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st">Google sign-in</div>
                  <div className="sd">Accept Google accounts.</div>
                </div>
                <label className="switch">
                  <input
                    type="checkbox"
                    checked={s.allow_google}
                    onChange={(e) => update({ allow_google: e.target.checked })}
                  />
                  <span className="slider" />
                </label>
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st">Microsoft sign-in</div>
                  <div className="sd">Accept Microsoft accounts.</div>
                </div>
                <label className="switch">
                  <input
                    type="checkbox"
                    checked={s.allow_microsoft}
                    onChange={(e) => update({ allow_microsoft: e.target.checked })}
                  />
                  <span className="slider" />
                </label>
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st">Default role for new users</div>
                  <div className="sd">Applied on first sign-in.</div>
                </div>
                <select
                  className="field-input"
                  value={s.default_role_id}
                  onChange={(e) => update({ default_role_id: Number(e.target.value) })}
                >
                  {roles.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.name}
                    </option>
                  ))}
                </select>
              </div>
            </>
          )}
        </Panel>

        <Panel
          title="// PENDING APPROVALS"
          right={<span className="role-badge">{pending.length}</span>}
        >
          {pending.length === 0 ? (
            <div className="online-empty">No sign-ups waiting for approval.</div>
          ) : (
            <div>
              {pending.map((u) => (
                <div className="pending-row" key={u.id}>
                  <span className="who">
                    <Link to={`/admin/users/${u.id}`}>{u.display_name ?? u.email}</Link>
                    <div className="muted small">{u.email}</div>
                  </span>
                  <RoleBadge role={u.role} />
                  <button className="btn btn-primary btn-sm" onClick={() => approve(u)}>
                    Approve
                  </button>
                </div>
              ))}
            </div>
          )}
        </Panel>
      </div>
    </div>
  );
}
