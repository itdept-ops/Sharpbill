import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { Panel } from "../components/Panel";
import { ProviderBadge, RoleBadge, StatusPill } from "../components/badges";
import type { ProfileUpdate, Role, User } from "../types";

const FIELDS: [keyof ProfileUpdate, string][] = [
  ["display_name", "Display name"],
  ["title", "Title"],
  ["department", "Department"],
  ["phone", "Phone"],
  ["location", "Location"],
  ["timezone", "Timezone"],
];

function initials(u: User): string {
  const base = u.display_name || u.email;
  return base.slice(0, 2).toUpperCase();
}

function StatusChip({ status }: { status: string }) {
  const cls = status === "active" ? "ok" : status === "pending" ? "info" : "off";
  const glyph = status === "active" ? "●" : status === "pending" ? "◆" : "✕";
  return <span className={`status-pill ${cls}`}>{glyph} {status}</span>;
}

export function UserProfile({ user: initial }: { user: User }) {
  const { user: me, setUser } = useAuth();
  const [user, setLocal] = useState<User>(initial);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<ProfileUpdate>({});
  const [roles, setRoles] = useState<Role[]>([]);

  useEffect(() => setLocal(initial), [initial]);

  const isSelf = me?.id === user.id;
  const canEdit = isSelf || !!me?.permissions.includes("users.manage");
  const canAdmin = !!me?.permissions.includes("users.manage") && !isSelf;
  const canKick = !!me?.permissions.includes("presence.kick") && !isSelf;
  const canPickRole =
    canAdmin && !!me?.permissions.includes("roles.manage") && !!me?.permissions.includes("users.manage");

  useEffect(() => {
    if (canPickRole) api.get<Role[]>("/api/roles").then(setRoles).catch(() => setRoles([]));
  }, [canPickRole]);

  const apply = (u: User) => {
    setLocal(u);
    if (isSelf) setUser(u); // keep the global session in sync when editing yourself
  };

  const startEdit = () => {
    setDraft({
      display_name: user.display_name,
      title: user.title,
      department: user.department,
      phone: user.phone,
      location: user.location,
      timezone: user.timezone,
      bio: user.bio,
    });
    setEditing(true);
  };

  const save = async () => {
    setBanner(null);
    try {
      apply(await api.patch<User>(`/api/users/${user.id}/profile`, draft));
      setEditing(false);
      setBanner({ msg: "Profile saved.", ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Save failed" });
    }
  };

  const adminAction = async (fn: () => Promise<User>, ok: string) => {
    setBanner(null);
    try {
      apply(await fn());
      setBanner({ msg: ok, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Action failed" });
    }
  };

  return (
    <div className="profile-grid">
      <Panel title="// IDENTITY" right={<StatusChip status={user.status} />}>
        <div className="profile-id">
          <div className="profile-avatar">{initials(user)}</div>
          <div>
            <div className="profile-name">{user.display_name ?? user.email}</div>
            <div className="muted small">{user.title ?? "—"}</div>
            <div style={{ marginTop: 6 }}>
              <RoleBadge role={user.role} /> <StatusPill online={user.online} />
            </div>
          </div>
        </div>
        <div className="kv">
          <div className="kv-row"><span className="k">Email</span><span className="v">{user.email}</span></div>
          <div className="kv-row"><span className="k">Department</span><span className="v">{user.department ?? "—"}</span></div>
          <div className="kv-row"><span className="k">Location</span><span className="v">{user.location ?? "—"}</span></div>
          <div className="kv-row"><span className="k">Timezone</span><span className="v">{user.timezone ?? "—"}</span></div>
          <div className="kv-row"><span className="k">Phone</span><span className="v">{user.phone ?? "—"}</span></div>
          <div className="kv-row"><span className="k">Providers</span><span className="v">{user.auth_providers.map((p) => <ProviderBadge key={p} provider={p} />)}</span></div>
          <div className="kv-row">
            <span className="k">Verified ID</span>
            <span className="v">
              {user.identities.map((i) => (
                <div key={i.provider + i.subject} className="mono-id" title={i.subject}>
                  {i.provider}:{i.subject.length > 16 ? `${i.subject.slice(0, 14)}…` : i.subject}
                </div>
              ))}
            </span>
          </div>
          <div className="kv-row"><span className="k">Joined</span><span className="v">{new Date(user.created_at).toLocaleDateString()}</span></div>
          <div className="kv-row"><span className="k">Last login</span><span className="v">{user.last_login_at ? new Date(user.last_login_at).toLocaleString() : "—"}</span></div>
        </div>
      </Panel>

      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {banner && (
          <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
            {banner.ok ? "" : "ERR: "}{banner.msg}
            <span className="spacer" />
            <button onClick={() => setBanner(null)}>✕</button>
          </div>
        )}

        <Panel
          title="// PROFILE"
          right={
            canEdit && !editing ? (
              <button className="icon-btn" onClick={startEdit}>Edit</button>
            ) : undefined
          }
        >
          {user.bio && !editing && <p className="sans dim" style={{ marginTop: 0 }}>{user.bio}</p>}
          {!editing ? (
            <div className="kv">
              {FIELDS.map(([k, label]) => (
                <div className="kv-row" key={k}>
                  <span className="k">{label}</span>
                  <span className="v">{(user[k as keyof User] as string) ?? "—"}</span>
                </div>
              ))}
            </div>
          ) : (
            <>
              <div className="form-grid">
                {FIELDS.map(([k, label]) => (
                  <div className="field" key={k}>
                    <label className="field-label">{label}</label>
                    <input
                      className="field-input"
                      value={(draft[k] as string) ?? ""}
                      onChange={(e) => setDraft({ ...draft, [k]: e.target.value })}
                    />
                  </div>
                ))}
                <div className="field full">
                  <label className="field-label">Bio</label>
                  <textarea
                    className="field-input"
                    rows={3}
                    value={draft.bio ?? ""}
                    onChange={(e) => setDraft({ ...draft, bio: e.target.value })}
                  />
                </div>
              </div>
              <div className="profile-actions">
                <button className="btn btn-primary btn-sm" onClick={save}>Save</button>
                <button className="btn btn-ghost btn-sm" onClick={() => setEditing(false)}>Cancel</button>
              </div>
            </>
          )}
        </Panel>

        {canAdmin && (
          <Panel title="// ADMIN CONTROLS">
            <div className="kv">
              {canPickRole && (
                <div className="kv-row" style={{ alignItems: "center" }}>
                  <span className="k">Role</span>
                  <span className="v">
                    <select
                      className="field-input"
                      value={user.role_id}
                      onChange={(e) =>
                        adminAction(
                          () => api.patch<User>(`/api/users/${user.id}/role`, { role_id: Number(e.target.value) }),
                          "Role updated.",
                        )
                      }
                    >
                      {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                    </select>
                  </span>
                </div>
              )}
              <div className="kv-row" style={{ alignItems: "center" }}>
                <span className="k">Active</span>
                <span className="v">
                  <label className="switch">
                    <input
                      type="checkbox"
                      checked={user.is_active}
                      onChange={(e) =>
                        adminAction(
                          () => api.patch<User>(`/api/users/${user.id}/status`, { is_active: e.target.checked }),
                          e.target.checked ? "Activated." : "Deactivated.",
                        )
                      }
                    />
                    <span className="slider" />
                  </label>
                </span>
              </div>
            </div>
            <div className="profile-actions">
              {user.status === "pending" && (
                <button
                  className="btn btn-primary btn-sm"
                  onClick={() => adminAction(() => api.post<User>(`/api/users/${user.id}/approve`), "Approved.")}
                >
                  Approve sign-up
                </button>
              )}
              {canKick && (
                <button
                  className="btn btn-danger btn-sm"
                  onClick={() => adminAction(() => api.post<User>(`/api/users/${user.id}/kick`), "Session revoked.")}
                >
                  Kick session
                </button>
              )}
            </div>
          </Panel>
        )}
      </div>
    </div>
  );
}
