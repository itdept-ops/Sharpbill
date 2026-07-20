import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { RoleBadge } from "../components/badges";
import { Panel } from "../components/Panel";
import type { Role, SignupMode, SiteSettings, User, UserList } from "../types";
import { applyCalm } from "../util/theme";

const MODES: { v: SignupMode; label: string; desc: string }[] = [
  { v: "open", label: "Open", desc: "Anyone who signs in gets an account immediately." },
  { v: "approval", label: "Approval", desc: "New sign-ins wait for an admin to approve them." },
  { v: "closed", label: "Closed", desc: "No new accounts can be created." },
];

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.message : fallback;
}

function wasAborted(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

export function SettingsPage() {
  const { user } = useAuth();
  const canReadRoles = !!user?.permissions.includes("roles.manage");
  const canReadUsers = !!user?.permissions.includes("users.read");
  const canApproveUsers = !!user?.permissions.includes("users.manage");
  const [settings, setSettings] = useState<SiteSettings | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [pending, setPending] = useState<User[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [saving, setSaving] = useState(false);
  const [approvingId, setApprovingId] = useState<number | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const opts = { signal: controller.signal };
    api
      .get<SiteSettings>("/api/admin/settings", opts)
      .then(setSettings)
      .catch((error) => {
        if (!wasAborted(error)) setBanner({ msg: errorMessage(error, "Failed to load settings") });
      });
    if (canReadRoles) {
      api
        .get<Role[]>("/api/roles", opts)
        .then(setRoles)
        .catch((error) => {
          if (!wasAborted(error)) setBanner({ msg: errorMessage(error, "Failed to load roles") });
        });
    } else {
      setRoles([]);
    }
    if (canReadUsers) {
      api
        .get<UserList>("/api/users?status=pending", opts)
        .then((result) => setPending(result.items))
        .catch((error) => {
          if (!wasAborted(error)) {
            setBanner({ msg: errorMessage(error, "Failed to load pending sign-ups") });
          }
        });
    } else {
      setPending([]);
    }
    return () => controller.abort();
  }, [canReadRoles, canReadUsers]);

  const update = async (patch: Partial<SiteSettings>) => {
    if (saving) return;
    setSaving(true);
    setBanner(null);
    try {
      setSettings(await api.put<SiteSettings>("/api/admin/settings", patch));
      setBanner({ msg: "Settings saved.", ok: true });
    } catch (error) {
      if (patch.calm_mode !== undefined && settings) applyCalm(settings.calm_mode);
      setBanner({ msg: errorMessage(error, "Save failed") });
    } finally {
      setSaving(false);
    }
  };

  const approve = async (pendingUser: User) => {
    if (!canApproveUsers || approvingId !== null) return;
    setApprovingId(pendingUser.id);
    setBanner(null);
    try {
      await api.post(`/api/users/${pendingUser.id}/approve`);
      setPending((current) => current.filter((candidate) => candidate.id !== pendingUser.id));
      setBanner({ msg: `Approved ${pendingUser.email}.`, ok: true });
    } catch (error) {
      setBanner({ msg: errorMessage(error, "Approve failed") });
    } finally {
      setApprovingId(null);
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
          <button aria-label="Dismiss notification" onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="settings-grid">
        <Panel title="// ACCESS">
          {!settings ? (
            <div className="muted" role="status">Loading…</div>
          ) : (
            <>
              <div className="set-row">
                <div className="set-label">
                  <div className="st" id="signup-mode-label">Sign-up mode</div>
                  <div className="sd">{MODES.find((mode) => mode.v === settings.signup_mode)?.desc}</div>
                </div>
              </div>
              <div
                className="mode-picker"
                style={{ marginBottom: 8 }}
                role="group"
                aria-labelledby="signup-mode-label"
              >
                {MODES.map((mode) => (
                  <button
                    key={mode.v}
                    className={`mode-opt ${settings.signup_mode === mode.v ? "active" : ""}`}
                    aria-pressed={settings.signup_mode === mode.v}
                    disabled={saving}
                    onClick={() => update({ signup_mode: mode.v })}
                  >
                    {mode.label}
                  </button>
                ))}
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st" id="allow-google-label">Google sign-in</div>
                  <div className="sd">Accept Google accounts.</div>
                </div>
                <label className="switch">
                  <input
                    type="checkbox"
                    aria-labelledby="allow-google-label"
                    checked={settings.allow_google}
                    disabled={saving}
                    onChange={(event) => update({ allow_google: event.target.checked })}
                  />
                  <span className="slider" />
                </label>
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st" id="allow-microsoft-label">Microsoft sign-in</div>
                  <div className="sd">Accept Microsoft accounts.</div>
                </div>
                <label className="switch">
                  <input
                    type="checkbox"
                    aria-labelledby="allow-microsoft-label"
                    checked={settings.allow_microsoft}
                    disabled={saving}
                    onChange={(event) => update({ allow_microsoft: event.target.checked })}
                  />
                  <span className="slider" />
                </label>
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st" id="default-role-label">Default role for new users</div>
                  <div className="sd">Applied on first sign-in.</div>
                </div>
                {canReadRoles ? (
                  <select
                    className="field-input"
                    aria-labelledby="default-role-label"
                    value={settings.default_role_id}
                    disabled={saving}
                    onChange={(event) => update({ default_role_id: Number(event.target.value) })}
                  >
                    {roles.map((role) => (
                      <option key={role.id} value={role.id}>{role.name}</option>
                    ))}
                  </select>
                ) : (
                  <span className="setting-readonly" title="Changing this requires roles.manage">
                    {settings.default_role_name}
                  </span>
                )}
              </div>
              <div className="set-row">
                <div className="set-label">
                  <div className="st" id="calm-mode-label">Calm mode</div>
                  <div className="sd">Site-wide: dim the code-rain and drop the scanlines for everyone.</div>
                </div>
                <label className="switch">
                  <input
                    type="checkbox"
                    aria-labelledby="calm-mode-label"
                    checked={settings.calm_mode}
                    disabled={saving}
                    onChange={(event) => {
                      applyCalm(event.target.checked);
                      update({ calm_mode: event.target.checked });
                    }}
                  />
                  <span className="slider" />
                </label>
              </div>
              {saving && <div className="muted small" role="status">Saving…</div>}
            </>
          )}
        </Panel>

        <Panel
          title="// PENDING APPROVALS"
          right={<span className="role-badge">{canReadUsers ? pending.length : "—"}</span>}
        >
          {!canReadUsers ? (
            <div className="permission-note">Requires <code>users.read</code> to view pending sign-ups.</div>
          ) : pending.length === 0 ? (
            <div className="online-empty">No sign-ups waiting for approval.</div>
          ) : (
            <div>
              {!canApproveUsers && (
                <div className="permission-note">Viewing only · approval requires <code>users.manage</code>.</div>
              )}
              {pending.map((pendingUser) => (
                <div className="pending-row" key={pendingUser.id}>
                  <span className="who">
                    <Link to={`/admin/users/${pendingUser.id}`}>
                      {pendingUser.display_name ?? pendingUser.email}
                    </Link>
                    <span className="muted small">{pendingUser.email}</span>
                  </span>
                  <RoleBadge role={pendingUser.role} />
                  {canApproveUsers && (
                    <button
                      className="btn btn-primary btn-sm"
                      disabled={approvingId !== null}
                      onClick={() => approve(pendingUser)}
                    >
                      {approvingId === pendingUser.id ? "Approving…" : "Approve"}
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
        </Panel>
      </div>
    </div>
  );
}
