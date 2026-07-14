import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import type { Presence } from "../types";

interface PresenceValue {
  online: Presence["online"];
  count: number;
  canView: boolean;
}

const PresenceContext = createContext<PresenceValue>({ online: [], count: 0, canView: false });

const POLL_MS = 20000;

export function PresenceProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const canView = !!user?.permissions.includes("presence.view");
  const [state, setState] = useState<PresenceValue>({ online: [], count: 0, canView });

  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        if (canView) {
          const p = await api.get<Presence>("/api/presence/online");
          if (alive) setState({ online: p.online, count: p.count, canView: true });
        } else {
          // No presence.view: still heartbeat so this user shows online to those who can see.
          await api.post("/api/presence/heartbeat");
          if (alive) setState({ online: [], count: 0, canView: false });
        }
      } catch {
        /* transient; keep last known */
      }
    };
    tick();
    const id = setInterval(tick, POLL_MS);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, [canView]);

  return <PresenceContext.Provider value={state}>{children}</PresenceContext.Provider>;
}

export function usePresence(): PresenceValue {
  return useContext(PresenceContext);
}
