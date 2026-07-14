import { useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { ProviderBadge, RoleBadge, StatusPill } from "../components/badges";
import type { Role, User, UserList } from "../types";

function shortId(s: string): string {
  return s.length > 10 ? `${s.slice(0, 8)}…` : s;
}

export function AdminUsersPage() {
  const { user: me } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [banner, setBanner] = useState<{ msg: string; ok?: boolean } | null>(null);
  const [loading, setLoading] = useState(true);

  const canManage = !!me?.permissions.includes("users.manage");
  const canManageRoles = !!me?.permissions.includes("roles.manage");
  const canKick = !!me?.permissions.includes("presence.kick");
  const canEditRole = canManage && canManageRoles;

  const load = () => {
    api
      .get<UserList>("/api/users")
      .then((r) => setUsers(r.items))
      .catch((e) => setBanner({ msg: e instanceof ApiError ? e.message : "Failed to load users" }))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    load();
    if (canManageRoles) api.get<Role[]>("/api/roles").then(setRoles).catch(() => setRoles([]));
  }, []);

  const replace = (u: User) => setUsers((rows) => rows.map((r) => (r.id === u.id ? u : r)));

  const act = async (fn: () => Promise<User>, prev: User) => {
    setBanner(null);
    try {
      replace(await fn());
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Action failed" });
      replace(prev); // revert
    }
  };

  const doKick = async (u: User) => {
    setBanner(null);
    try {
      replace(await api.post<User>(`/api/users/${u.id}/kick`));
      setBanner({ msg: `Kicked ${u.email}. Their session is now revoked.`, ok: true });
    } catch (e) {
      setBanner({ msg: e instanceof ApiError ? e.message : "Kick failed" });
    }
  };

  return (
    <div>
      <h1 className="page-title">SYS://admin / users</h1>
      <p className="page-sub">
        {users.length} record{users.length === 1 ? "" : "s"} · roles read fresh from the database
        every request
      </p>

      {banner && (
        <div className={`banner ${banner.ok ? "ok" : ""}`} role="alert">
          {banner.ok ? "" : "ERR: "}
          {banner.msg}
          <span className="spacer" />
          <button onClick={() => setBanner(null)}>✕</button>
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Email</th>
              <th>Name</th>
              <th>Providers</th>
              <th>Provider ID (verified)</th>
              <th>Role</th>
              <th>Active</th>
              <th style={{ textAlign: "right" }}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading && (
              <tr>
                <td colSpan={8} className="muted">
                  Loading…
                </td>
              </tr>
            )}
            {!loading && users.length === 0 && (
              <tr>
                <td colSpan={8} className="muted">
                  No users yet.
                </td>
              </tr>
            )}
            {users.map((u) => {
              const isSelf = u.id === me?.id;
              return (
                <tr key={u.id} className={u.is_active ? "" : "row-inactive"}>
                  <td>
                    <StatusPill online={u.online} />
                  </td>
                  <td>
                    {u.email}
                    {isSelf && <span className="muted"> (you)</span>}
                  </td>
                  <td className="sans">{u.display_name ?? "—"}</td>
                  <td>
                    {u.auth_providers.map((p) => (
                      <ProviderBadge key={p} provider={p} />
                    ))}
                  </td>
                  <td>
                    {u.identities.map((i) => (
                      <div key={i.provider + i.subject} className="mono-id" title={i.subject}>
                        {i.provider}:{shortId(i.subject)}
                      </div>
                    ))}
                  </td>
                  <td>
                    {canEditRole && !isSelf ? (
                      <select
                        className="field-input"
                        value={u.role_id}
                        onChange={(e) =>
                          act(
                            () =>
                              api.patch<User>(`/api/users/${u.id}/role`, {
                                role_id: Number(e.target.value),
                              }),
                            u,
                          )
                        }
                      >
                        {roles.map((r) => (
                          <option key={r.id} value={r.id}>
                            {r.name}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <RoleBadge role={u.role} />
                    )}
                  </td>
                  <td>
                    <label className="switch" title={isSelf ? "You cannot deactivate yourself" : ""}>
                      <input
                        type="checkbox"
                        checked={u.is_active}
                        disabled={!canManage || isSelf}
                        onChange={(e) =>
                          act(
                            () =>
                              api.patch<User>(`/api/users/${u.id}/status`, {
                                is_active: e.target.checked,
                              }),
                            u,
                          )
                        }
                      />
                      <span className="slider" />
                    </label>
                  </td>
                  <td>
                    <div className="row-actions">
                      {canKick && (
                        <button
                          className="icon-btn danger"
                          disabled={isSelf}
                          title={isSelf ? "You cannot kick yourself" : "Force sign-out"}
                          onClick={() => doKick(u)}
                        >
                          Kick
                        </button>
                      )}
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
