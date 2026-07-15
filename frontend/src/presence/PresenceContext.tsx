import { createContext, type ReactNode, useContext, useEffect, useState } from "react";

import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import type { Presence, PresenceUser } from "../types";

interface PresenceValue {
  online: PresenceUser[];
  count: number;
  canView: boolean;
  live: boolean; // true when the real-time WebSocket is connected
}

const PresenceContext = createContext<PresenceValue>({
  online: [],
  count: 0,
  canView: false,
  live: false,
});

const POLL_MS = 20000;
const PING_MS = 25000;

export function PresenceProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const canView = !!user?.permissions.includes("presence.view");
  const [state, setState] = useState<PresenceValue>({ online: [], count: 0, canView, live: false });

  useEffect(() => {
    if (!user) return;
    let ws: WebSocket | null = null;
    let pollTimer: ReturnType<typeof setInterval> | null = null;
    let pingTimer: ReturnType<typeof setInterval> | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let reconnectDelay = 1000; // bounded exponential backoff, reset on a successful open
    let disposed = false;

    const scheduleReconnect = () => {
      if (disposed || reconnectTimer) return;
      reconnectTimer = setTimeout(() => {
        reconnectTimer = null;
        reconnectDelay = Math.min(reconnectDelay * 2, 30000);
        connect();
      }, reconnectDelay);
    };

    const stopPolling = () => {
      if (pollTimer) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
    };

    // Fallback path if the WebSocket can't connect.
    const startPolling = () => {
      if (pollTimer || disposed) return;
      const tick = async () => {
        try {
          if (canView) {
            const p = await api.get<Presence>("/api/presence/online");
            if (!disposed) setState({ online: p.online, count: p.count, canView: true, live: false });
          } else {
            await api.post("/api/presence/heartbeat");
          }
        } catch {
          /* keep last known */
        }
      };
      tick();
      pollTimer = setInterval(tick, POLL_MS);
    };

    const connect = () => {
      try {
        const proto = window.location.protocol === "https:" ? "wss" : "ws";
        ws = new WebSocket(`${proto}://${window.location.host}/api/ws/presence`);
      } catch {
        startPolling();
        return;
      }
      ws.onopen = () => {
        stopPolling();
        reconnectDelay = 1000; // healthy connection — reset backoff
        pingTimer = setInterval(() => {
          if (ws && ws.readyState === WebSocket.OPEN) ws.send("ping");
        }, PING_MS);
      };
      ws.onmessage = (ev) => {
        try {
          const msg = JSON.parse(ev.data);
          if (msg.type === "presence" && !disposed) {
            setState({ online: msg.online ?? [], count: msg.count ?? 0, canView, live: true });
          }
        } catch {
          /* ignore malformed frames */
        }
      };
      ws.onclose = () => {
        if (pingTimer) {
          clearInterval(pingTimer);
          pingTimer = null;
        }
        if (!disposed) {
          setState((s) => ({ ...s, live: false }));
          startPolling(); // keep data flowing via polling...
          scheduleReconnect(); // ...while trying to re-establish the live socket
        }
      };
      ws.onerror = () => {
        try {
          ws?.close();
        } catch {
          /* noop */
        }
      };
    };

    connect();
    return () => {
      disposed = true;
      stopPolling();
      if (pingTimer) clearInterval(pingTimer);
      if (reconnectTimer) clearTimeout(reconnectTimer);
      try {
        ws?.close();
      } catch {
        /* noop */
      }
    };
  }, [user, canView]);

  return <PresenceContext.Provider value={state}>{children}</PresenceContext.Provider>;
}

export function usePresence(): PresenceValue {
  return useContext(PresenceContext);
}
