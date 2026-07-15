import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { SessionsPanel } from "../components/SessionsPanel";
import { UserProfile } from "../components/UserProfile";
import type { User } from "../types";

export function UserDetailPage() {
  const { id } = useParams();
  const { user: me } = useAuth();
  const [user, setUser] = useState<User | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    setErr(null);
    api
      .get<User>(`/api/users/${id}`)
      .then(setUser)
      .catch((e) => setErr(e instanceof ApiError ? e.message : "Not found"));
  }, [id]);

  return (
    <div>
      <h1 className="page-title">
        <Link to="/admin/users" className="link-btn">
          ← users
        </Link>{" "}
        / detail
      </h1>
      {err && <div className="banner">ERR: {err}</div>}
      {user ? <UserProfile user={user} /> : !err && <div className="muted">Loading…</div>}
      {user && (
        <div style={{ marginTop: 16 }}>
          <SessionsPanel
            title="// SESSIONS · DEVICES"
            listUrl={`/api/users/${id}/sessions`}
            revokeUrl={(sid) => `/api/users/${id}/sessions/${sid}`}
            canRevoke={!!me?.permissions.includes("presence.kick")}
          />
        </div>
      )}
    </div>
  );
}
