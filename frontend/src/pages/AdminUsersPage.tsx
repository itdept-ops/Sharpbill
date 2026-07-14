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

export function AdminUsersPage() {
  const { user: me } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [loading, setLoading] = useState(true);

  const [search, setSearch] = useState("");
  const [roleId, setRoleId] = useState("");
  const [status, setStatus] = useState("");
  const [onlineOnly, setOnlineOnly] = useState(false);

  const canManage = !!me?.permissions.includes("users.manage");
  const canManageRoles = !!me?.permissions.includes("roles.manage");
  const canEditRole = canManage && canManageRoles;

  const load = useCallback(() => {
    const q = new URLSearchParams();
    if (search.trim()) q.set("search", search.trim());
    if (roleId) q.set("role_id", roleId);
    if (status) q.set("status", status);
    if (onlineOnly) q.set("online", "true");
    setLoading(true);
    api
      .get<UserList>(`/api/users?${q.toString()}`)
      .then((r) => setUsers(r.items))
      .catch((e) => setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load" }))
      .finally(() => setLoading(false));
  }, [search, roleId, status, onlineOnly]);

  useEffect(() => {
    const t = setTimeout(load, 200); // debounce search typing
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
        {(search || roleId || status || onlineOnly) && (
          <button
            className="link-btn"
            onClick={() => {
              setSearch("");
              setRoleId("");
              setStatus("");
              setOnlineOnly(false);
            }}
          >
            clear filters
          </button>
        )}
      </div>

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
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
              <tr><td colSpan={7} className="muted">Loading…</td></tr>
            )}
            {!loading && users.length === 0 && (
              <tr><td colSpan={7} className="muted">No users match.</td></tr>
            )}
            {users.map((u) => {
              const isSelf = u.id === me?.id;
              return (
                <tr key={u.id} className={u.is_active && u.is_approved ? "" : "row-inactive"}>
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
                          onClick={() =>
                            act(() => api.post<User>(`/api/users/${u.id}/approve`), u, `Approved ${u.email}.`)
                          }
                        >
                          Approve
                        </button>
                      )}
                      <Link className="icon-btn" to={`/admin/users/${u.id}`}>
                        View
                      </Link>
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
