import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { RoleBadge } from "../components/badges";
import { Panel } from "../components/Panel";
import type {
  PrivacyAdminStatus,
  Role,
  SignupMode,
  SiteSettings,
  User,
  UserList,
} from "../types";
import { applyCalm } from "../util/theme";

const MODES: { v: SignupMode; label: string; desc: string }[] = [
  {
    v: "open",
    label: "Open",
    desc: "Any user cryptographically verified by an enabled provider gets an account immediately with the configured default role.",
  },
  {
    v: "approval",
    label: "Approval",
    desc: "Any user cryptographically verified by an enabled provider can request an account; it stays pending until an admin approves it.",
  },
  {
    v: "closed",
    label: "Closed",
    desc: "Only existing accounts can sign in. New accounts are blocked except for the configured first-admin bootstrap.",
  },
];

const HOLD_REFERENCE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:/-]{2,254}$/;

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
  const canManagePrivacy = !!user?.permissions.includes("privacy.manage");
  const [settings, setSettings] = useState<SiteSettings | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [pending, setPending] = useState<User[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [saving, setSaving] = useState(false);
  const [approvingId, setApprovingId] = useState<number | null>(null);
  const [privacyAdmin, setPrivacyAdmin] = useState<PrivacyAdminStatus | null>(null);
  const [holdReference, setHoldReference] = useState("");
  const [privacySaving, setPrivacySaving] = useState(false);

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
    if (canManagePrivacy) {
      api
        .get<PrivacyAdminStatus>("/api/admin/privacy", opts)
        .then((status) => {
          setPrivacyAdmin(status);
          setHoldReference(status.retention_hold_reference ?? "");
        })
        .catch((error) => {
          if (!wasAborted(error)) {
            setBanner({ msg: errorMessage(error, "Failed to load retention-hold status") });
          }
        });
    } else {
      setPrivacyAdmin(null);
      setHoldReference("");
    }
    return () => controller.abort();
  }, [canManagePrivacy, canReadRoles, canReadUsers]);

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

  const updateRetentionHold = async (enabled: boolean) => {
    if (!privacyAdmin || privacySaving) return;
    const reference = holdReference.trim();
    if (enabled && !HOLD_REFERENCE_PATTERN.test(reference)) {
      setBanner({
        msg: "Enter a 3â€“255 character external case reference using letters, numbers, . _ : / or -",
      });
      return;
    }
    const confirmed = window.confirm(
      enabled
        ? `Enable the global retention hold for ${reference}? Automated deletion and new erasure requests will be suspended.`
        : `Release the global retention hold ${privacyAdmin.retention_hold_reference ?? ""}? Automated retention and due erasures will resume.`,
    );
    if (!confirmed) return;

    setPrivacySaving(true);
    setBanner(null);
    try {
      const next = await api.put<PrivacyAdminStatus>(
        "/api/admin/privacy/hold",
        enabled ? { enabled: true, reference } : { enabled: false },
      );
      setPrivacyAdmin(next);
      setHoldReference(next.retention_hold_reference ?? "");
      setBanner({ msg: enabled ? "Retention hold enabled." : "Retention hold released.", ok: true });
    } catch (error) {
      setBanner({ msg: errorMessage(error, "Retention hold update failed") });
    } finally {
      setPrivacySaving(false);
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://admin / settings</h1>
      <p className="page-sub">
        Sign-up mode is the admission policy for every enabled provider. Email domains are not
        restricted.
      </p>

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
              {settings.signup_mode === "open" && (
                <div className="permission-note">
                  Open is broad: every cryptographically verified user from an enabled provider can
                  create an account, with no email-domain restriction. Keep the default role
                  least-privileged.
                </div>
              )}
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

        {canManagePrivacy && (
          <Panel
            title="// RETENTION HOLD"
            right={
              privacyAdmin ? (
                <span className={`status-pill ${privacyAdmin.retention_hold ? "info" : "ok"}`}>
                  {privacyAdmin.retention_hold ? "ACTIVE" : "INACTIVE"}
                </span>
              ) : undefined
            }
          >
            {!privacyAdmin ? (
              <div className="muted" role="status">Loading retention-hold statusâ€¦</div>
            ) : privacyAdmin.retention_hold ? (
              <>
                <div className="permission-note" role="status">
                  Global deletion is suspended under external case{" "}
                  <code>{privacyAdmin.retention_hold_reference}</code>. Releasing the hold resumes
                  automated retention and account erasures that are due.
                </div>
                <div className="profile-actions">
                  <button
                    type="button"
                    className="btn btn-danger btn-sm"
                    disabled={privacySaving}
                    onClick={() => updateRetentionHold(false)}
                  >
                    {privacySaving ? "Releasingâ€¦" : "Release retention hold"}
                  </button>
                </div>
              </>
            ) : (
              <>
                <div className="field">
                  <label className="field-label" htmlFor="retention-hold-reference">
                    External case reference
                  </label>
                  <input
                    id="retention-hold-reference"
                    className="field-input"
                    value={holdReference}
                    minLength={3}
                    maxLength={255}
                    pattern="[A-Za-z0-9][A-Za-z0-9._:/-]{2,254}"
                    autoComplete="off"
                    aria-describedby="retention-hold-reference-help"
                    disabled={privacySaving}
                    onChange={(event) => setHoldReference(event.target.value)}
                  />
                  <div className="permission-note" id="retention-hold-reference-help">
                    Use a terse ticket or legal-case key onlyâ€”not evidence or personal data.
                    Enabling the hold suspends governed deletion across this deployment.
                  </div>
                </div>
                <div className="profile-actions">
                  <button
                    type="button"
                    className="btn btn-danger btn-sm"
                    disabled={privacySaving || !HOLD_REFERENCE_PATTERN.test(holdReference.trim())}
                    onClick={() => updateRetentionHold(true)}
                  >
                    {privacySaving ? "Enablingâ€¦" : "Enable retention hold"}
                  </button>
                </div>
              </>
            )}
          </Panel>
        )}
      </div>
    </div>
  );
}
