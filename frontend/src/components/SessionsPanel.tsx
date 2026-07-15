import { useCallback, useEffect, useState } from "react";

import { api, ApiError } from "../api/client";
import { Panel } from "./Panel";
import type { SessionInfo } from "../types";

function deviceLabel(ua: string | null): string {
  if (!ua) return "Unknown device";
  const browser = /Edg/.test(ua)
    ? "Edge"
    : /Chrome/.test(ua)
      ? "Chrome"
      : /Firefox/.test(ua)
        ? "Firefox"
        : /Safari/.test(ua)
          ? "Safari"
          : "Browser";
  const os = /Windows/.test(ua)
    ? "Windows"
    : /Mac OS/.test(ua)
      ? "macOS"
      : /Android/.test(ua)
        ? "Android"
        : /iPhone|iPad/.test(ua)
          ? "iOS"
          : /Linux/.test(ua)
            ? "Linux"
            : "";
  return os ? `${browser} · ${os}` : browser;
}

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : "—");

/**
 * Lists active sessions (one per signed-in device) and revokes them individually. Used for the
 * user's own devices (self-service) and, for admins, to manage another user's sessions.
 */
export function SessionsPanel({
  listUrl,
  revokeUrl,
  canRevoke = true,
  title = "// SESSIONS",
}: {
  listUrl: string;
  revokeUrl: (id: number) => string;
  canRevoke?: boolean;
  title?: string;
}) {
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [msg, setMsg] = useState<string | null>(null);

  const load = useCallback(() => {
    api
      .get<SessionInfo[]>(listUrl)
      .then(setSessions)
      .catch((e) => setMsg(e instanceof ApiError ? e.message : "Failed to load sessions"));
  }, [listUrl]);

  useEffect(() => {
    load();
  }, [load]);

  const revoke = async (s: SessionInfo) => {
    setMsg(null);
    try {
      await api.del(revokeUrl(s.id));
      setSessions((prev) => prev.filter((x) => x.id !== s.id));
    } catch (e) {
      setMsg(e instanceof ApiError ? e.message : "Revoke failed");
    }
  };

  return (
    <Panel title={title} right={<span className="muted small">{sessions.length} active</span>}>
      {msg && (
        <div className="banner" role="alert">
          ERR: {msg}
          <span className="spacer" />
          <button aria-label="Dismiss" onClick={() => setMsg(null)}>
            ✕
          </button>
        </div>
      )}
      {sessions.length === 0 ? (
        <div className="muted small">No active sessions.</div>
      ) : (
        <div className="session-list">
          {sessions.map((s) => (
            <div className="session-row" key={s.id}>
              <div>
                <div className="session-device">
                  {deviceLabel(s.user_agent)}
                  {s.current && <span className="session-current">● THIS DEVICE</span>}
                </div>
                <div className="muted small">
                  {s.ip ?? "—"} · last active {when(s.last_seen_at ?? s.created_at)}
                </div>
              </div>
              <span className="spacer" />
              {canRevoke && !s.current && (
                <button className="btn btn-danger btn-sm" onClick={() => revoke(s)}>
                  Revoke
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </Panel>
  );
}
