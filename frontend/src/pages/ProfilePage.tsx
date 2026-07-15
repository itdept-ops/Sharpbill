import { useEffect, useState } from "react";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { AppearancePanel } from "../components/AppearancePanel";
import { SessionsPanel } from "../components/SessionsPanel";
import { UserProfile } from "../components/UserProfile";
import type { User } from "../types";

export function ProfilePage() {
  const { user: me } = useAuth();
  const [user, setUser] = useState<User | null>(null);

  useEffect(() => {
    if (me) api.get<User>(`/api/users/${me.id}`).then(setUser).catch(() => setUser(me));
  }, [me]);

  return (
    <div>
      <h1 className="page-title">SYS://profile</h1>
      <p className="page-sub">Your account, identity, and profile — edit anything below.</p>
      {user ? <UserProfile user={user} /> : <div className="muted">Loading…</div>}
      <div style={{ marginTop: 16 }}>
        <AppearancePanel />
      </div>
      <div style={{ marginTop: 16 }}>
        <SessionsPanel
          title="// SESSIONS · YOUR DEVICES"
          listUrl="/api/auth/sessions"
          revokeUrl={(id) => `/api/auth/sessions/${id}`}
        />
      </div>
    </div>
  );
}
