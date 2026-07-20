import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { Panel } from "../components/Panel";
import type { Permission, Role } from "../types";
import { useAccessibleDialog } from "../util/useAccessibleDialog";

interface RoleDraft {
  mode: "create" | "edit";
  id?: number;
  name: string;
  description: string;
  isSystem: boolean;
  keys: string[];
  version?: number;
}

export function AdminRolesPage() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [perms, setPerms] = useState<Permission[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [roleDraft, setRoleDraft] = useState<RoleDraft | null>(null);
  const [permDraft, setPermDraft] = useState<{ key: string; description: string } | null>(null);
  const [busyRoles, setBusyRoles] = useState<Set<number>>(new Set());
  const [savingRole, setSavingRole] = useState(false);
  const [savingPermission, setSavingPermission] = useState(false);

  const loadAll = () => {
    api.get<Role[]>("/api/roles").then(setRoles).catch((e) => fail(e, "load roles"));
    api.get<Permission[]>("/api/permissions").then(setPerms).catch(() => setPerms([]));
  };
  useEffect(loadAll, []);

  const fail = (e: unknown, what: string) =>
    setBanner({ msg: e instanceof ApiError ? e.message : `Failed to ${what}` });

  const toggleInline = async (role: Role, key: string, on: boolean) => {
    if (busyRoles.has(role.id)) return;
    const keys = on
      ? [...role.permissions.map((p) => p.key), key]
      : role.permissions.map((p) => p.key).filter((k) => k !== key);
    setBanner(null);
    setBusyRoles((current) => new Set(current).add(role.id));
    try {
      const updated = await api.patch<Role>(`/api/roles/${role.id}`, {
        permission_keys: keys,
        expected_version: role.version,
      });
      setRoles((rs) => rs.map((r) => (r.id === updated.id ? updated : r)));
    } catch (e) {
      fail(e, "update role");
    } finally {
      setBusyRoles((current) => {
        const next = new Set(current);
        next.delete(role.id);
        return next;
      });
    }
  };

  const saveRole = async () => {
    if (!roleDraft || savingRole) return;
    setSavingRole(true);
    setBanner(null);
    try {
      if (roleDraft.mode === "create") {
        await api.post<Role>("/api/roles", {
          name: roleDraft.name,
          description: roleDraft.description || null,
          permission_keys: roleDraft.keys,
        });
      } else {
        await api.patch<Role>(`/api/roles/${roleDraft.id}`, {
          name: roleDraft.isSystem ? undefined : roleDraft.name,
          description: roleDraft.description,
          permission_keys: roleDraft.keys,
          expected_version: roleDraft.version,
        });
      }
      setRoleDraft(null);
      loadAll();
      setBanner({ msg: "Role saved.", ok: true });
    } catch (e) {
      fail(e, "save role");
    } finally {
      setSavingRole(false);
    }
  };

  const deleteRole = async (role: Role) => {
    setBanner(null);
    try {
      await api.del(`/api/roles/${role.id}?expected_version=${role.version}`);
      setRoles((rs) => rs.filter((r) => r.id !== role.id));
      setBanner({ msg: `Deleted role "${role.name}".`, ok: true });
    } catch (e) {
      fail(e, "delete role");
    }
  };

  const savePerm = async () => {
    if (!permDraft || savingPermission) return;
    setSavingPermission(true);
    setBanner(null);
    try {
      await api.post<Permission>("/api/permissions", {
        key: permDraft.key,
        description: permDraft.description || null,
      });
      setPermDraft(null);
      loadAll();
      setBanner({ msg: "Permission created.", ok: true });
    } catch (e) {
      fail(e, "create permission");
    } finally {
      setSavingPermission(false);
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://admin / roles &amp; access</h1>
      <p className="page-sub">
        Define roles, mint permissions, and assign them. Access is enforced from these records on
        every request.
      </p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button aria-label="Dismiss notification" onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="toolbar">
        <button
          className="btn btn-primary btn-sm"
          onClick={() =>
            setRoleDraft({ mode: "create", name: "", description: "", isSystem: false, keys: [] })
          }
        >
          + New role
        </button>
        <button
          className="btn btn-ghost btn-sm"
          onClick={() => setPermDraft({ key: "", description: "" })}
        >
          + New permission
        </button>
        <span className="spacer" />
        <span className="muted" style={{ fontSize: 11 }}>
          {roles.length} roles · {perms.length} permissions
        </span>
      </div>

      <div className="roles-grid">
        {roles.map((role) => {
          const locked = role.name === "admin"; // fully locked
          const has = new Set(role.permissions.map((p) => p.key));
          return (
            <Panel
              key={role.id}
              className="role-card"
              title={`// ${role.name.toUpperCase()}`}
              right={
                <span className="muted" style={{ fontSize: 10 }}>
                  {role.is_system ? "SYSTEM" : "CUSTOM"} · {role.user_count} user
                  {role.user_count === 1 ? "" : "s"}
                </span>
              }
            >
              {role.description && <div className="role-desc">{role.description}</div>}
              {locked && <div className="muted" style={{ fontSize: 11 }}>🔒 The admin role is locked.</div>}
              <div className="perm-list">
                {perms.map((p) => (
                  <label className="perm-check" key={p.id}>
                    <input
                      type="checkbox"
                      checked={has.has(p.key)}
                      disabled={locked || busyRoles.has(role.id)}
                      onChange={(e) => toggleInline(role, p.key, e.target.checked)}
                    />
                    <span>
                      <span className="pk">{p.key}</span>
                      {p.description && <div className="pd">{p.description}</div>}
                    </span>
                  </label>
                ))}
              </div>
              {!locked && (
                <div className="role-card-actions">
                  <button
                    className="icon-btn"
                    onClick={() =>
                      setRoleDraft({
                        mode: "edit",
                        id: role.id,
                        name: role.name,
                        description: role.description ?? "",
                        isSystem: role.is_system,
                        keys: role.permissions.map((p) => p.key),
                        version: role.version,
                      })
                    }
                  >
                    Edit
                  </button>
                  {!role.is_system && (
                    <button className="icon-btn danger" onClick={() => deleteRole(role)}>
                      Delete
                    </button>
                  )}
                </div>
              )}
            </Panel>
          );
        })}
      </div>

      {roleDraft && (
        <RoleModal
          draft={roleDraft}
          perms={perms}
          onChange={setRoleDraft}
          onClose={() => setRoleDraft(null)}
          onSave={saveRole}
          saving={savingRole}
        />
      )}
      {permDraft && (
        <PermissionModal
          draft={permDraft}
          saving={savingPermission}
          onChange={setPermDraft}
          onClose={() => setPermDraft(null)}
          onSave={savePerm}
        />
      )}
    </div>
  );
}

function PermissionModal({
  draft,
  saving,
  onChange,
  onClose,
  onSave,
}: {
  draft: { key: string; description: string };
  saving: boolean;
  onChange: (draft: { key: string; description: string }) => void;
  onClose: () => void;
  onSave: () => void;
}) {
  const panelRef = useAccessibleDialog(onClose);
  return (
    <div className="modal">
      <button className="modal-dismiss" type="button" aria-label="Close dialog" onClick={onClose} />
      <section
        ref={panelRef}
        className="panel panel--brackets modal-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="permission-dialog-title"
        tabIndex={-1}
      >
        <div className="panel-header" id="permission-dialog-title">// NEW PERMISSION</div>
        <div className="panel-body">
          <div className="field">
            <label className="field-label" htmlFor="permission-key">&gt; key (area.action)</label>
            <input
              id="permission-key"
              className="field-input"
              placeholder="reports.export"
              value={draft.key}
              disabled={saving}
              onChange={(event) => onChange({ ...draft, key: event.target.value })}
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="permission-description">&gt; description</label>
            <input
              id="permission-description"
              className="field-input"
              value={draft.description}
              disabled={saving}
              onChange={(event) => onChange({ ...draft, description: event.target.value })}
            />
          </div>
          <div className="role-card-actions">
            <button className="btn btn-primary btn-sm" disabled={saving} onClick={onSave}>
              {saving ? "Creating…" : "Create"}
            </button>
            <button className="btn btn-ghost btn-sm" disabled={saving} onClick={onClose}>Cancel</button>
          </div>
        </div>
      </section>
    </div>
  );
}

function RoleModal({
  draft,
  perms,
  onChange,
  onClose,
  onSave,
  saving,
}: {
  draft: RoleDraft;
  perms: Permission[];
  onChange: (d: RoleDraft) => void;
  onClose: () => void;
  onSave: () => void;
  saving: boolean;
}) {
  const panelRef = useAccessibleDialog(onClose);
  const has = new Set(draft.keys);
  const toggle = (key: string, on: boolean) =>
    onChange({ ...draft, keys: on ? [...draft.keys, key] : draft.keys.filter((k) => k !== key) });

  const groups: Record<string, Permission[]> = {};
  for (const p of perms) {
    const area = p.key.split(".")[0];
    (groups[area] ??= []).push(p);
  }
  const setGroup = (list: Permission[], on: boolean) => {
    const keys = new Set(draft.keys);
    for (const p of list) {
      if (on) keys.add(p.key);
      else keys.delete(p.key);
    }
    onChange({ ...draft, keys: [...keys] });
  };

  return (
    <div className="modal">
      <button className="modal-dismiss" type="button" aria-label="Close dialog" onClick={onClose} />
      <section
        ref={panelRef}
        className="panel panel--brackets modal-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="role-dialog-title"
        tabIndex={-1}
      >
        <div className="panel-header">
          <span id="role-dialog-title">// {draft.mode === "create" ? "NEW ROLE" : `EDIT ${draft.name.toUpperCase()}`}</span>
          <span className="muted" style={{ fontSize: 10 }}>
            {draft.keys.length} / {perms.length} perms
          </span>
        </div>
        <div className="panel-body">
          <div className="form-row">
            <div className="field">
              <label className="field-label" htmlFor="role-name">&gt; name</label>
              <input
                id="role-name"
                className="field-input"
                value={draft.name}
                disabled={draft.isSystem || saving}
                onChange={(e) => onChange({ ...draft, name: e.target.value })}
              />
            </div>
          </div>
          <div className="field">
            <label className="field-label" htmlFor="role-description">&gt; description</label>
            <input
              id="role-description"
              className="field-input"
              value={draft.description}
              disabled={saving}
              onChange={(e) => onChange({ ...draft, description: e.target.value })}
            />
          </div>
          <div className="field-label">&gt; permissions · {draft.keys.length} selected</div>
          <div className="perm-groups">
            {Object.entries(groups).map(([area, list]) => {
              const allOn = list.every((p) => has.has(p.key));
              return (
                <div className="perm-group" key={area}>
                  <div className="perm-group-head">
                    <span className="pg-area">{area}</span>
                    <button className="link-btn" disabled={saving} onClick={() => setGroup(list, !allOn)}>
                      {allOn ? "clear all" : "select all"}
                    </button>
                  </div>
                  {list.map((p) => (
                    <label className="perm-check" key={p.id}>
                      <input
                        type="checkbox"
                        checked={has.has(p.key)}
                        disabled={saving}
                        onChange={(e) => toggle(p.key, e.target.checked)}
                      />
                      <span>
                        <span className="pk">{p.key}</span>
                        {p.description && <div className="pd">{p.description}</div>}
                      </span>
                    </label>
                  ))}
                </div>
              );
            })}
          </div>
          <div className="role-card-actions">
            <button className="btn btn-primary btn-sm" disabled={saving} onClick={onSave}>
              {saving ? "Saving…" : "Save role"}
            </button>
            <button className="btn btn-ghost btn-sm" disabled={saving} onClick={onClose}>
              Cancel
            </button>
          </div>
        </div>
      </section>
    </div>
  );
}
