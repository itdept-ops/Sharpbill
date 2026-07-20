import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { Panel } from "../components/Panel";
import { ProviderBadge, RoleBadge, StatusPill } from "../components/badges";
import type { PrivacyStatus, ProfileUpdate, Role, User } from "../types";

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

function formatUtcDateTime(value: string): string {
  // API timestamps are UTC, while database-backed datetimes may serialize without a zone suffix.
  const normalized = /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`;
  return new Date(normalized).toLocaleString();
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
  const [privacy, setPrivacy] = useState<PrivacyStatus | null>(null);
  const [privacyError, setPrivacyError] = useState<string | null>(null);
  const [privacyAction, setPrivacyAction] = useState<"location" | "request" | "cancel" | null>(null);

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

  useEffect(() => {
    if (!isSelf) {
      setPrivacy(null);
      setPrivacyError(null);
      return;
    }
    let active = true;
    setPrivacyError(null);
    api
      .get<PrivacyStatus>("/api/privacy")
      .then((status) => {
        if (active) setPrivacy(status);
      })
      .catch((error) => {
        if (active) setPrivacyError(error instanceof ApiError ? error.message : "Privacy settings failed to load");
      });
    return () => {
      active = false;
    };
  }, [isSelf]);

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

  const clearSavedLocation = async () => {
    if (
      !window.confirm(
        "Clear your saved location and timezone now? This removes the stored values immediately and cannot be undone.",
      )
    ) {
      return;
    }
    setPrivacyAction("location");
    setBanner(null);
    try {
      await api.del<void>("/api/privacy/location");
      apply({
        ...user,
        location: null,
        timezone: null,
        last_latitude: null,
        last_longitude: null,
        last_location_accuracy: null,
        last_location_at: null,
      });
      setBanner({ msg: "Saved location and timezone cleared.", ok: true });
    } catch (error) {
      setBanner({ msg: error instanceof ApiError ? error.message : "Location could not be cleared" });
    } finally {
      setPrivacyAction(null);
    }
  };

  const requestErasure = async () => {
    if (!privacy) return;
    if (
      !window.confirm(
        `Schedule account erasure in ${privacy.policy.erasure_grace_days} days? Your profile and personal data will be anonymized. You can cancel before the due date.`,
      )
    ) {
      return;
    }
    setPrivacyAction("request");
    setBanner(null);
    try {
      setPrivacy(await api.post<PrivacyStatus>("/api/privacy/erasure-request"));
      setBanner({ msg: "Account erasure scheduled. You can cancel before the due date.", ok: true });
    } catch (error) {
      setBanner({ msg: error instanceof ApiError ? error.message : "Erasure request failed" });
    } finally {
      setPrivacyAction(null);
    }
  };

  const cancelErasure = async () => {
    if (!window.confirm("Cancel the scheduled account erasure and keep this account?")) return;
    setPrivacyAction("cancel");
    setBanner(null);
    try {
      setPrivacy(await api.del<PrivacyStatus>("/api/privacy/erasure-request"));
      setBanner({ msg: "Account erasure cancelled.", ok: true });
    } catch (error) {
      setBanner({ msg: error instanceof ApiError ? error.message : "Erasure cancellation failed" });
    } finally {
      setPrivacyAction(null);
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
          <div className="kv-row"><span className="k">Joined</span><span className="v">{new Date(user.created_at).toLocaleDateString()}</span></div>
          <div className="kv-row"><span className="k">Last login</span><span className="v">{user.last_login_at ? new Date(user.last_login_at).toLocaleString() : "—"}</span></div>
          {user.last_location_at && user.last_latitude != null && user.last_longitude != null && (
            <div className="kv-row">
              <span className="k">Last location</span>
              <span className="v">
                <a
                  href={`https://www.google.com/maps?q=${user.last_latitude},${user.last_longitude}`}
                  target="_blank"
                  rel="noreferrer"
                >
                  {user.last_latitude.toFixed(4)}, {user.last_longitude.toFixed(4)}
                </a>
                {user.last_location_accuracy != null && (
                  <span className="muted"> ±{Math.round(user.last_location_accuracy)}m</span>
                )}
                <div className="muted small">{new Date(user.last_location_at).toLocaleString()}</div>
              </span>
            </div>
          )}
        </div>
      </Panel>

      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {banner && (
          <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
            {banner.ok ? "" : "ERR: "}{banner.msg}
            <span className="spacer" />
            <button aria-label="Dismiss notification" onClick={() => setBanner(null)}>✕</button>
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
                    <label className="field-label" htmlFor={`profile-${k}`}>{label}</label>
                    <input
                      id={`profile-${k}`}
                      className="field-input"
                      value={(draft[k] as string) ?? ""}
                      onChange={(e) => setDraft({ ...draft, [k]: e.target.value })}
                    />
                  </div>
                ))}
                <div className="field full">
                  <label className="field-label" htmlFor="profile-bio">Bio</label>
                  <textarea
                    id="profile-bio"
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

        {isSelf && (
          <Panel title="// PRIVACY">
            {privacyError ? (
              <div className="auth-error" role="alert">ERR: {privacyError}</div>
            ) : !privacy ? (
              <div className="muted" role="status">Loading privacy settingsâ€¦</div>
            ) : (
              <>
                {privacy.retention_hold && (
                  <div className="permission-note" role="status">
                    A retention hold is active. Location deletion and new erasure requests are
                    suspended; an existing erasure request can still be cancelled.
                  </div>
                )}
                <div className="kv">
                  <div className="kv-row">
                    <span className="k">Saved location</span>
                    <span className="v">
                      {user.last_location_at || user.location || user.timezone
                        ? `Stored; precise coordinates expire within ${privacy.policy.precise_location_hours} hours.`
                        : "No location or timezone is saved."}
                    </span>
                  </div>
                  <div className="kv-row">
                    <span className="k">Account erasure</span>
                    <span className="v">
                      {privacy.erasure_due_at ? (
                        <>
                          Scheduled for{" "}
                          <time dateTime={privacy.erasure_due_at}>
                            {formatUtcDateTime(privacy.erasure_due_at)}
                          </time>
                          .
                        </>
                      ) : user.role === "admin" ? (
                        "Administrator accounts cannot be scheduled for erasure."
                      ) : (
                        `Not scheduled; requests have a ${privacy.policy.erasure_grace_days}-day cancellation window.`
                      )}
                    </span>
                  </div>
                </div>
                <div className="profile-actions">
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm"
                    disabled={
                      privacyAction !== null ||
                      privacy.retention_hold ||
                      !(user.last_location_at || user.location || user.timezone)
                    }
                    onClick={clearSavedLocation}
                  >
                    {privacyAction === "location" ? "Clearingâ€¦" : "Clear saved location"}
                  </button>
                  {privacy.erasure_due_at ? (
                    <button
                      type="button"
                      className="btn btn-ghost btn-sm"
                      disabled={privacyAction !== null}
                      onClick={cancelErasure}
                    >
                      {privacyAction === "cancel" ? "Cancellingâ€¦" : "Cancel account erasure"}
                    </button>
                  ) : (
                    <button
                      type="button"
                      className="btn btn-danger btn-sm"
                      disabled={privacyAction !== null || privacy.retention_hold || user.role === "admin"}
                      onClick={requestErasure}
                    >
                      {privacyAction === "request" ? "Schedulingâ€¦" : "Request account erasure"}
                    </button>
                  )}
                </div>
              </>
            )}
          </Panel>
        )}

        {canAdmin && (
          <Panel title="// ADMIN CONTROLS">
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
                        adminAction(
                          () =>
                            api.patch<User>(`/api/users/${user.id}/role`, {
                              role_id: Number(e.target.value),
                              expected_version: user.access_version,
                            }),
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
                      aria-label="Active account"
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
