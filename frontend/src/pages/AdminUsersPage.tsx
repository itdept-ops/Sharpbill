import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { ProviderBadge } from "../components/ProviderBadge";
import type { Role, User, UserList } from "../types";

export function AdminUsersPage() {
  const { user: me } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [banner, setBanner] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = () => {
    api
      .get<UserList>("/api/users")
      .then((r) => setUsers(r.items))
      .catch((e) => setBanner(e instanceof ApiError ? e.message : "Failed to load users"))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const replaceRow = (updated: User) =>
    setUsers((rows) => rows.map((u) => (u.id === updated.id ? updated : u)));

  const changeRole = async (u: User, role: Role) => {
    setBanner(null);
    try {
      replaceRow(await api.patch<User>(`/api/users/${u.id}/role`, { role }));
    } catch (e) {
      setBanner(e instanceof ApiError ? e.message : "Update failed");
      replaceRow(u); // revert
    }
  };

  const changeStatus = async (u: User, is_active: boolean) => {
    setBanner(null);
    try {
      replaceRow(await api.patch<User>(`/api/users/${u.id}/status`, { is_active }));
    } catch (e) {
      setBanner(e instanceof ApiError ? e.message : "Update failed");
      replaceRow(u); // revert
    }
  };

  return (
    <div className="page">
      <h1 className="page-title">User management</h1>
      <p className="muted">
        {users.length} user{users.length === 1 ? "" : "s"} · role changes apply on the user&apos;s
        next request
      </p>

      {banner && (
        <div className="banner error" role="alert">
          {banner}
          <button className="banner-close" onClick={() => setBanner(null)}>
            ✕
          </button>
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Email</th>
              <th>Name</th>
              <th>Providers</th>
              <th>Role</th>
              <th>Active</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {loading && (
              <tr>
                <td colSpan={6} className="muted">
                  Loading…
                </td>
              </tr>
            )}
            {!loading && users.length === 0 && (
              <tr>
                <td colSpan={6} className="muted">
                  No users yet.
                </td>
              </tr>
            )}
            {users.map((u) => {
              const isSelf = u.id === me?.id;
              return (
                <tr key={u.id} className={u.is_active ? "" : "row-inactive"}>
                  <td>
                    {u.email}
                    {isSelf && <span className="muted small"> (you)</span>}
                  </td>
                  <td>{u.display_name ?? "—"}</td>
                  <td>
                    {u.auth_providers.map((p) => (
                      <ProviderBadge key={p} provider={p} />
                    ))}
                  </td>
                  <td>
                    <select
                      value={u.role}
                      disabled={isSelf}
                      title={isSelf ? "You cannot change your own role" : undefined}
                      onChange={(e) => changeRole(u, e.target.value as Role)}
                    >
                      <option value="user">user</option>
                      <option value="admin">admin</option>
                    </select>
                  </td>
                  <td>
                    <label className="switch" title={isSelf ? "You cannot deactivate yourself" : undefined}>
                      <input
                        type="checkbox"
                        checked={u.is_active}
                        disabled={isSelf}
                        onChange={(e) => changeStatus(u, e.target.checked)}
                      />
                      <span className="slider" />
                    </label>
                  </td>
                  <td>{new Date(u.created_at).toLocaleDateString()}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
