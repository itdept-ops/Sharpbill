import { type CSSProperties, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { Panel } from "./Panel";
import type { User } from "../types";
import { applyAccent } from "../util/theme";

const DEFAULT = "#35ff74";
const PRESETS: [string, string][] = [
  ["Green", "#35ff74"],
  ["Teal", "#19e5d0"],
  ["Sky", "#38bdf8"],
  ["Violet", "#9085e9"],
  ["Magenta", "#f472b6"],
  ["Amber", "#ffc24b"],
  ["Orange", "#ff8a4c"],
  ["Red", "#ff5a47"],
  ["Lime", "#a3e635"],
];

/** Per-user accent-color selector — recolors the whole console (stays dark). */
export function ThemePicker() {
  const { user, setUser } = useAuth();
  const [msg, setMsg] = useState<string | null>(null);
  if (!user) return null;
  const current = (user.accent_color ?? DEFAULT).toLowerCase();

  const choose = async (hex: string | null) => {
    applyAccent(hex ?? DEFAULT); // instant feedback
    try {
      const updated = await api.patch<User>(`/api/users/${user.id}/profile`, { accent_color: hex });
      setUser(updated);
      setMsg(null);
    } catch (e) {
      applyAccent(user.accent_color); // revert on failure
      setMsg(e instanceof ApiError ? e.message : "Failed to save");
    }
  };

  return (
    <Panel
      title="// THEME · ACCENT"
      right={
        user.accent_color ? (
          <button className="link-btn" onClick={() => choose(null)}>
            reset
          </button>
        ) : undefined
      }
    >
      {msg && <div className="banner">ERR: {msg}</div>}
      <p className="muted small" style={{ marginTop: 0 }}>
        Recolor the console accent — it stays dark, only the accent changes.
      </p>
      <div className="swatch-row">
        {PRESETS.map(([name, hex]) => (
          <button
            key={hex}
            className={`swatch ${current === hex.toLowerCase() ? "active" : ""}`}
            style={{ "--sw": hex } as CSSProperties}
            title={name}
            aria-label={name}
            onClick={() => choose(hex)}
          />
        ))}
        <label className="swatch swatch-custom" title="Custom color" aria-label="Custom color">
          <input
            type="color"
            value={current}
            onInput={(e) => applyAccent((e.target as HTMLInputElement).value)}
            onChange={(e) => choose(e.target.value)}
          />
        </label>
      </div>
    </Panel>
  );
}
