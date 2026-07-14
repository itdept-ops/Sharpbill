import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { Panel } from "../components/Panel";
import type { Permission, Role } from "../types";

interface RoleDraft {
  mode: "create" | "edit";
  id?: number;
  name: string;
  description: string;
  isSystem: boolean;
  keys: string[];
}

export function AdminRolesPage() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [perms, setPerms] = useState<Permission[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [roleDraft, setRoleDraft] = useState<RoleDraft | null>(null);
  const [permDraft, setPermDraft] = useState<{ key: string; description: string } | null>(null);

  const loadAll = () => {
    api.get<Role[]>("/api/roles").then(setRoles).catch((e) => fail(e, "load roles"));
    api.get<Permission[]>("/api/permissions").then(setPerms).catch(() => setPerms([]));
  };
  useEffect(loadAll, []);

  const fail = (e: unknown, what: string) =>
    setBanner({ msg: e instanceof ApiError ? e.message : `Failed to ${what}` });

  const toggleInline = async (role: Role, key: string, on: boolean) => {
    const keys = on
      ? [...role.permissions.map((p) => p.key), key]
      : role.permissions.map((p) => p.key).filter((k) => k !== key);
    setBanner(null);
    try {
      const updated = await api.patch<Role>(`/api/roles/${role.id}`, { permission_keys: keys });
      setRoles((rs) => rs.map((r) => (r.id === updated.id ? updated : r)));
    } catch (e) {
      fail(e, "update role");
    }
  };

  const saveRole = async () => {
    if (!roleDraft) return;
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
        });
      }
      setRoleDraft(null);
      loadAll();
      setBanner({ msg: "Role saved.", ok: true });
    } catch (e) {
      fail(e, "save role");
    }
  };

  const deleteRole = async (role: Role) => {
    setBanner(null);
    try {
      await api.del(`/api/roles/${role.id}`);
      setRoles((rs) => rs.filter((r) => r.id !== role.id));
      setBanner({ msg: `Deleted role "${role.name}".`, ok: true });
    } catch (e) {
      fail(e, "delete role");
    }
  };

  const savePerm = async () => {
    if (!permDraft) return;
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
          <button onClick={() => setBanner(null)}>✕</button>
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
                      disabled={locked}
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
        />
      )}
      {permDraft && (
        <div className="modal" onClick={() => setPermDraft(null)}>
          <section
            className="panel panel--brackets modal-panel"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="panel-header">// NEW PERMISSION</div>
            <div className="panel-body">
              <div className="field">
                <label className="field-label">&gt; key (area.action)</label>
                <input
                  className="field-input"
                  placeholder="reports.export"
                  value={permDraft.key}
                  onChange={(e) => setPermDraft({ ...permDraft, key: e.target.value })}
                />
              </div>
              <div className="field">
                <label className="field-label">&gt; description</label>
                <input
                  className="field-input"
                  value={permDraft.description}
                  onChange={(e) => setPermDraft({ ...permDraft, description: e.target.value })}
                />
              </div>
              <div className="role-card-actions">
                <button className="btn btn-primary btn-sm" onClick={savePerm}>
                  Create
                </button>
                <button className="btn btn-ghost btn-sm" onClick={() => setPermDraft(null)}>
                  Cancel
                </button>
              </div>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

function RoleModal({
  draft,
  perms,
  onChange,
  onClose,
  onSave,
}: {
  draft: RoleDraft;
  perms: Permission[];
  onChange: (d: RoleDraft) => void;
  onClose: () => void;
  onSave: () => void;
}) {
  const has = new Set(draft.keys);
  const toggle = (key: string, on: boolean) =>
    onChange({ ...draft, keys: on ? [...draft.keys, key] : draft.keys.filter((k) => k !== key) });

  return (
    <div className="modal" onClick={onClose}>
      <section className="panel panel--brackets modal-panel" onClick={(e) => e.stopPropagation()}>
        <div className="panel-header">
          // {draft.mode === "create" ? "NEW ROLE" : `EDIT ${draft.name.toUpperCase()}`}
        </div>
        <div className="panel-body">
          <div className="form-row">
            <div className="field">
              <label className="field-label">&gt; name</label>
              <input
                className="field-input"
                value={draft.name}
                disabled={draft.isSystem}
                onChange={(e) => onChange({ ...draft, name: e.target.value })}
              />
            </div>
          </div>
          <div className="field">
            <label className="field-label">&gt; description</label>
            <input
              className="field-input"
              value={draft.description}
              onChange={(e) => onChange({ ...draft, description: e.target.value })}
            />
          </div>
          <div className="field-label">&gt; permissions</div>
          <div className="perm-list">
            {perms.map((p) => (
              <label className="perm-check" key={p.id}>
                <input
                  type="checkbox"
                  checked={has.has(p.key)}
                  onChange={(e) => toggle(p.key, e.target.checked)}
                />
                <span>
                  <span className="pk">{p.key}</span>
                  {p.description && <div className="pd">{p.description}</div>}
                </span>
              </label>
            ))}
          </div>
          <div className="role-card-actions">
            <button className="btn btn-primary btn-sm" onClick={onSave}>
              Save
            </button>
            <button className="btn btn-ghost btn-sm" onClick={onClose}>
              Cancel
            </button>
          </div>
        </div>
      </section>
    </div>
  );
}
