import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { ProviderBadge, RoleBadge } from "./badges";
import type { Permission, ProfileUpdate, Role, User } from "../types";
import { useAccessibleDialog } from "../util/useAccessibleDialog";

const FIELDS: [keyof ProfileUpdate, string][] = [
  ["display_name", "Display name"],
  ["title", "Title"],
  ["department", "Department"],
  ["phone", "Phone"],
  ["location", "Location"],
  ["timezone", "Timezone"],
];

/**
 * Focused, single-column editor for a user. Opens with every field pre-populated. Email and the
 * verified provider ID are shown read-only in the header — identity is keyed to the immutable
 * provider subject, so those are intentionally not editable.
 */
export function UserEditModal({
  user: initial,
  roles,
  onClose,
  onChange,
}: {
  user: User;
  roles: Role[];
  onClose: () => void;
  onChange: (u: User) => void;
}) {
  const { user: me } = useAuth();
  const panelRef = useAccessibleDialog(onClose);
  const [user, setUser] = useState<User>(initial);
  const [draft, setDraft] = useState<ProfileUpdate>({
    display_name: initial.display_name,
    title: initial.title,
    department: initial.department,
    phone: initial.phone,
    location: initial.location,
    timezone: initial.timezone,
    bio: initial.bio,
  });
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [saving, setSaving] = useState(false);

  const isSelf = me?.id === user.id;
  const canManage = !!me?.permissions.includes("users.manage");
  const canEditProfile = isSelf || canManage;
  const canPickRole = canManage && !isSelf && !!me?.permissions.includes("roles.manage");
  const canToggleActive = canManage && !isSelf;
  const canKick = !isSelf && !!me?.permissions.includes("presence.kick");
  const hasAdmin = canPickRole || canToggleActive || canKick || (canManage && user.status === "pending");

  const push = (u: User) => {
    setUser(u);
    onChange(u);
  };

  const saveProfile = async () => {
    setSaving(true);
    setBanner(null);
    try {
      push(await api.patch<User>(`/api/users/${user.id}/profile`, draft));
      setBanner({ msg: "Profile saved.", ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Save failed" });
    } finally {
      setSaving(false);
    }
  };

  const action = async (fn: () => Promise<User>, ok: string) => {
    setBanner(null);
    try {
      push(await fn());
      setBanner({ msg: ok, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Action failed" });
    }
  };

  // Direct per-user permission grants (on top of the role). Needs users.manage + roles.manage.
  const canManagePerms = canManage && !isSelf && !!me?.permissions.includes("roles.manage");
  const [allPerms, setAllPerms] = useState<Permission[]>([]);
  const [savingPermissions, setSavingPermissions] = useState(false);
  useEffect(() => {
    if (canManagePerms)
      api.get<Permission[]>("/api/permissions").then(setAllPerms).catch(() => setAllPerms([]));
  }, [canManagePerms]);

  const roleKeys = new Set(user.role_permissions);
  const directKeys = new Set(user.direct_permissions);
  const togglePerm = async (key: string) => {
    if (savingPermissions) return;
    const next = new Set(directKeys);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    setSavingPermissions(true);
    try {
      await action(
        () => api.put<User>(`/api/users/${user.id}/permissions`, { permission_keys: [...next] }),
        "Permissions updated.",
      );
    } finally {
      setSavingPermissions(false);
    }
  };
  const permGroups = allPerms.reduce<Record<string, Permission[]>>((acc, p) => {
    (acc[p.key.split(".")[0]] ??= []).push(p);
    return acc;
  }, {});

  const initials = (user.display_name || user.email).slice(0, 2).toUpperCase();

  return (
    <div className="modal">
      <button className="modal-dismiss" type="button" aria-label="Close dialog" onClick={onClose} />
      <section
        ref={panelRef}
        className="panel panel--brackets modal-panel edit-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="edit-user-title"
        tabIndex={-1}
      >
        <div className="panel-header">
          <span id="edit-user-title">// EDIT USER</span>
          <span className="spacer" />
          <button className="icon-btn" onClick={onClose}>
            ✕ close
          </button>
        </div>
        <div className="panel-body">
          <div className="edit-id-head">
            <div className="profile-avatar">{initials}</div>
            <div>
              <div className="profile-name">{user.display_name ?? user.email}</div>
              <div className="muted small">{user.email}</div>
              <div className="edit-id-badges">
                <RoleBadge role={user.role} />
                {user.auth_providers.map((p) => (
                  <ProviderBadge key={p} provider={p} />
                ))}
                {user.identities.map((i) => (
                  <span key={i.provider + i.subject} className="mono-id" title={i.subject}>
                    {i.provider}:{i.subject.length > 16 ? `${i.subject.slice(0, 14)}…` : i.subject}
                  </span>
                ))}
              </div>
            </div>
          </div>

          {banner && (
            <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
              {banner.ok ? "" : "ERR: "}
              {banner.msg}
              <span className="spacer" />
              <button aria-label="Dismiss" onClick={() => setBanner(null)}>
                ✕
              </button>
            </div>
          )}

          <div className="edit-section">
            <div className="edit-section-title">Profile</div>
            <div className="form-grid">
              {FIELDS.map(([k, label]) => (
                <div className="field" key={k}>
                  <label className="field-label" htmlFor={`edit-${k}`}>
                    {label}
                  </label>
                  <input
                    id={`edit-${k}`}
                    className="field-input"
                    value={(draft[k] as string) ?? ""}
                    disabled={!canEditProfile}
                    onChange={(e) => setDraft({ ...draft, [k]: e.target.value })}
                  />
                </div>
              ))}
              <div className="field full">
                <label className="field-label" htmlFor="edit-bio">
                  Bio
                </label>
                <textarea
                  id="edit-bio"
                  className="field-input"
                  rows={3}
                  value={draft.bio ?? ""}
                  disabled={!canEditProfile}
                  onChange={(e) => setDraft({ ...draft, bio: e.target.value })}
                />
              </div>
            </div>
            {canEditProfile && (
              <div className="profile-actions">
                <button className="btn btn-primary btn-sm" onClick={saveProfile} disabled={saving}>
                  {saving ? "Saving…" : "Save profile"}
                </button>
              </div>
            )}
          </div>

          {hasAdmin && (
            <div className="edit-section">
              <div className="edit-section-title">Access &amp; status</div>
              <div className="kv">
                {canPickRole && (
                  <div className="kv-row" style={{ alignItems: "center" }}>
                    <span className="k">Role</span>
                    <span className="v">
                      <select
                        className="field-input"
                        aria-label="Role"
                        value={user.role_id}
                        onChange={(e) =>
                          action(
                            () =>
                              api.patch<User>(`/api/users/${user.id}/role`, {
                                role_id: Number(e.target.value),
                              }),
                            "Role updated.",
                          )
                        }
                      >
                        {roles.map((r) => (
                          <option key={r.id} value={r.id}>
                            {r.name}
                          </option>
                        ))}
                      </select>
                    </span>
                  </div>
                )}
                {canToggleActive && (
                  <div className="kv-row" style={{ alignItems: "center" }}>
                    <span className="k">Active</span>
                    <span className="v">
                      <label className="switch">
                        <input
                          type="checkbox"
                          aria-label="Active account"
                          checked={user.is_active}
                          onChange={(e) =>
                            action(
                              () =>
                                api.patch<User>(`/api/users/${user.id}/status`, {
                                  is_active: e.target.checked,
                                }),
                              e.target.checked ? "Activated." : "Deactivated.",
                            )
                          }
                        />
                        <span className="slider" />
                      </label>
                    </span>
                  </div>
                )}
              </div>
              <div className="profile-actions">
                {canManage && user.status === "pending" && (
                  <button
                    className="btn btn-primary btn-sm"
                    onClick={() =>
                      action(() => api.post<User>(`/api/users/${user.id}/approve`), "Approved.")
                    }
                  >
                    Approve sign-up
                  </button>
                )}
                {canKick && (
                  <button
                    className="btn btn-danger btn-sm"
                    onClick={() =>
                      action(() => api.post<User>(`/api/users/${user.id}/kick`), "Session revoked.")
                    }
                  >
                    Kick session
                  </button>
                )}
              </div>
            </div>
          )}

          {canManagePerms && (
            <div className="edit-section">
              <div className="edit-section-title">
                Direct permissions <span className="muted small">· on top of the role</span>
              </div>
              <div className="perm-grant-groups">
                {Object.entries(permGroups).map(([group, perms]) => (
                  <div className="perm-grant-group" key={group}>
                    <div className="perm-grant-head">{group}</div>
                    {perms.map((p) => {
                      const viaRole = roleKeys.has(p.key);
                      const direct = directKeys.has(p.key);
                      return (
                        <label className="perm-grant" key={p.key} title={p.description ?? p.key}>
                          <input
                            type="checkbox"
                            checked={viaRole || direct}
                            disabled={viaRole || savingPermissions}
                            onChange={() => togglePerm(p.key)}
                          />
                          <span className="perm-grant-key">{p.key}</span>
                          {viaRole ? (
                            <span className="perm-src">via role</span>
                          ) : direct ? (
                            <span className="perm-src direct">direct</span>
                          ) : null}
                        </label>
                      );
                    })}
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
