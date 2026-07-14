import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { ProviderBadge, RoleBadge } from "../components/badges";
import type { Role, User, UserList } from "../types";

function statusChip(u: User) {
  if (u.status === "pending") return <span className="status-pill info">◆ PENDING</span>;
  if (u.status === "disabled") return <span className="status-pill off">✕ DISABLED</span>;
  return u.online ? (
    <span className="status-pill ok">● ONLINE</span>
  ) : (
    <span className="status-pill off">○ OFFLINE</span>
  );
}

interface BulkResult {
  applied: number;
  results: { id: number; ok: boolean; error?: string }[];
}

export function AdminUsersPage() {
  const { user: me } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState<Set<number>>(new Set());

  const [search, setSearch] = useState("");
  const [roleId, setRoleId] = useState("");
  const [status, setStatus] = useState("");
  const [onlineOnly, setOnlineOnly] = useState(false);

  const canManage = !!me?.permissions.includes("users.manage");
  const canManageRoles = !!me?.permissions.includes("roles.manage");
  const canEditRole = canManage && canManageRoles;

  const query = useCallback(() => {
    const q = new URLSearchParams();
    if (search.trim()) q.set("search", search.trim());
    if (roleId) q.set("role_id", roleId);
    if (status) q.set("status", status);
    if (onlineOnly) q.set("online", "true");
    return q;
  }, [search, roleId, status, onlineOnly]);

  const load = useCallback(() => {
    setLoading(true);
    api
      .get<UserList>(`/api/users?${query().toString()}`)
      .then((r) => setUsers(r.items))
      .catch((e) => setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load" }))
      .finally(() => setLoading(false));
  }, [query]);

  useEffect(() => {
    const t = setTimeout(load, 200);
    return () => clearTimeout(t);
  }, [load]);

  useEffect(() => {
    if (canManageRoles) api.get<Role[]>("/api/roles").then(setRoles).catch(() => setRoles([]));
  }, [canManageRoles]);

  const replace = (u: User) => setUsers((rows) => rows.map((r) => (r.id === u.id ? u : r)));

  const act = async (fn: () => Promise<User>, prev: User, okMsg?: string) => {
    setBanner(null);
    try {
      replace(await fn());
      if (okMsg) setBanner({ msg: okMsg, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Action failed" });
      replace(prev);
    }
  };

  const toggleSel = (id: number) =>
    setSelected((s) => {
      const n = new Set(s);
      if (n.has(id)) n.delete(id);
      else n.add(id);
      return n;
    });
  const allSelected = users.length > 0 && users.every((u) => selected.has(u.id));
  const toggleAll = () =>
    setSelected(allSelected ? new Set() : new Set(users.map((u) => u.id)));

  const bulk = async (action: string, role_id?: number) => {
    const ids = [...selected];
    if (!ids.length) return;
    setBanner(null);
    try {
      const r = await api.post<BulkResult>("/api/users/bulk", { ids, action, role_id });
      setSelected(new Set());
      load();
      const failed = r.results.filter((x) => !x.ok).length;
      setBanner({ msg: `${r.applied} updated${failed ? `, ${failed} skipped` : ""}.`, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Bulk action failed" });
    }
  };

  const exportCsv = async () => {
    try {
      const res = await fetch(`/api/users/export.csv?${query().toString()}`, { credentials: "same-origin" });
      if (!res.ok) throw new Error();
      const url = URL.createObjectURL(await res.blob());
      const a = document.createElement("a");
      a.href = url;
      a.download = "users.csv";
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setBanner({ msg: "Export failed" });
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://admin / users</h1>
      <p className="page-sub">{users.length} record{users.length === 1 ? "" : "s"} shown</p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="filters">
        <input
          className="field-input search"
          placeholder="search email or name…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        {canManageRoles && (
          <select className="field-input" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
            <option value="">all roles</option>
            {roles.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
        )}
        <select className="field-input" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">all status</option>
          <option value="active">active</option>
          <option value="pending">pending</option>
          <option value="disabled">disabled</option>
        </select>
        <label className="user-chip" style={{ gap: 6 }}>
          <input type="checkbox" checked={onlineOnly} onChange={(e) => setOnlineOnly(e.target.checked)} />
          online only
        </label>
        <span className="spacer" />
        <button className="icon-btn" onClick={exportCsv}>
          ⭳ Export CSV
        </button>
      </div>

      {canManage && selected.size > 0 && (
        <div className="bulkbar">
          <span className="bulk-count">{selected.size} selected</span>
          <button className="btn btn-ghost btn-sm" onClick={() => bulk("approve")}>Approve</button>
          <button className="btn btn-ghost btn-sm" onClick={() => bulk("activate")}>Activate</button>
          <button className="btn btn-danger btn-sm" onClick={() => bulk("deactivate")}>Deactivate</button>
          {canEditRole && (
            <select
              className="field-input"
              defaultValue=""
              onChange={(e) => e.target.value && bulk("assign_role", Number(e.target.value))}
            >
              <option value="">assign role…</option>
              {roles.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </select>
          )}
          <span className="spacer" />
          <button className="link-btn" onClick={() => setSelected(new Set())}>clear</button>
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              {canManage && (
                <th style={{ width: 28 }}>
                  <input type="checkbox" checked={allSelected} onChange={toggleAll} />
                </th>
              )}
              <th>Status</th>
              <th>Email</th>
              <th>Name</th>
              <th>Providers</th>
              <th>Verified ID</th>
              <th>Role</th>
              <th style={{ textAlign: "right" }}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading && (
              <tr><td colSpan={canManage ? 8 : 7} className="muted">Loading…</td></tr>
            )}
            {!loading && users.length === 0 && (
              <tr><td colSpan={canManage ? 8 : 7} className="muted">No users match.</td></tr>
            )}
            {users.map((u) => {
              const isSelf = u.id === me?.id;
              return (
                <tr key={u.id} className={u.is_active && u.is_approved ? "" : "row-inactive"}>
                  {canManage && (
                    <td>
                      <input
                        type="checkbox"
                        checked={selected.has(u.id)}
                        onChange={() => toggleSel(u.id)}
                        disabled={isSelf}
                      />
                    </td>
                  )}
                  <td>{statusChip(u)}</td>
                  <td>
                    <Link to={`/admin/users/${u.id}`}>{u.email}</Link>
                    {isSelf && <span className="muted"> (you)</span>}
                  </td>
                  <td className="sans">{u.display_name ?? "—"}</td>
                  <td>{u.auth_providers.map((p) => <ProviderBadge key={p} provider={p} />)}</td>
                  <td>
                    {u.identities.map((i) => (
                      <div key={i.provider + i.subject} className="mono-id" title={i.subject}>
                        {i.provider}:{i.subject.length > 12 ? `${i.subject.slice(0, 10)}…` : i.subject}
                      </div>
                    ))}
                  </td>
                  <td>
                    {canEditRole && !isSelf ? (
                      <select
                        className="field-input"
                        value={u.role_id}
                        onChange={(e) =>
                          act(() => api.patch<User>(`/api/users/${u.id}/role`, { role_id: Number(e.target.value) }), u)
                        }
                      >
                        {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                      </select>
                    ) : (
                      <RoleBadge role={u.role} />
                    )}
                  </td>
                  <td>
                    <div className="row-actions">
                      {canManage && u.status === "pending" && (
                        <button
                          className="icon-btn"
                          onClick={() => act(() => api.post<User>(`/api/users/${u.id}/approve`), u, `Approved ${u.email}.`)}
                        >
                          Approve
                        </button>
                      )}
                      <Link className="icon-btn" to={`/admin/users/${u.id}`}>View</Link>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
