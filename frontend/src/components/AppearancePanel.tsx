import { type CSSProperties, useState } from "react";

import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { Panel } from "./Panel";
import type { UiPrefs, User } from "../types";
import { applyAccent, DEFAULT_RAIN_DENSITY } from "../util/theme";

const ACCENT_DEFAULT = "#35ff74";
const ACCENTS: [string, string][] = [
  ["Green", "#35ff74"],
  ["Teal", "#19e5d0"],
  ["Sky", "#38bdf8"],
  ["Violet", "#9085e9"],
  ["Magenta", "#f472b6"],
  ["Amber", "#ffc24b"],
  ["Orange", "#fb923c"],
  ["Red", "#ff5a47"],
  ["Lime", "#a3e635"],
];

// key -> its default value (used to highlight the active segment when the pref is unset).
const DEFAULTS: Record<string, string> = {
  base_tone: "abyss",
  background_depth: "standard",
  border_glow: "standard",
  glow_intensity: "normal",
  scanlines: "standard",
  corner_radius: "sharp",
  motion: "full",
  rain_speed: "normal",
  rain_glyphs: "katakana",
  font_family: "system",
  text_scale: "100",
  density: "comfortable",
  focus_ring: "standard",
};

type Seg = { key: keyof UiPrefs; label: string; opts: [string, string][] };

const COLOR: Seg[] = [
  { key: "base_tone", label: "Base tone", opts: [["abyss", "Abyss"], ["ink", "Ink"], ["graphite", "Graphite"], ["midnight", "Midnight"], ["warm-black", "Warm"]] },
  { key: "background_depth", label: "Depth", opts: [["pure-black", "OLED"], ["standard", "Standard"], ["elevated", "Elevated"]] },
  { key: "border_glow", label: "Borders", opts: [["hairline", "Hairline"], ["standard", "Standard"], ["neon", "Neon"]] },
];
const TEXTURE: Seg[] = [
  { key: "glow_intensity", label: "Accent glow", opts: [["off", "Off"], ["subtle", "Subtle"], ["normal", "Normal"], ["intense", "Intense"]] },
  { key: "scanlines", label: "Scanlines", opts: [["off", "Off"], ["subtle", "Subtle"], ["standard", "Standard"], ["heavy", "Heavy"]] },
  { key: "corner_radius", label: "Corners", opts: [["sharp", "Sharp"], ["soft", "Soft"], ["round", "Round"]] },
];
const MOTION: Seg[] = [
  { key: "motion", label: "Motion", opts: [["full", "Full"], ["calm", "Calm"], ["reduced", "Reduced"]] },
  { key: "rain_speed", label: "Rain speed", opts: [["still", "Still"], ["slow", "Slow"], ["normal", "Normal"], ["fast", "Fast"]] },
  { key: "rain_glyphs", label: "Rain glyphs", opts: [["katakana", "カナ"], ["ascii", "AB"], ["binary", "01"], ["hex", "0F"]] },
];
const TYPO: Seg[] = [
  { key: "font_family", label: "Terminal font", opts: [["system", "System"], ["high-legibility", "Legible"], ["cascadia", "Cascadia"], ["jetbrains", "JetBrains"], ["consolas", "Consolas"], ["menlo", "Menlo"]] },
  { key: "text_scale", label: "Text size", opts: [["90", "90%"], ["100", "100%"], ["112", "112%"], ["125", "125%"]] },
  { key: "density", label: "Density", opts: [["compact", "Compact"], ["comfortable", "Comfortable"], ["spacious", "Spacious"]] },
];
const A11Y_SEG: Seg[] = [
  { key: "focus_ring", label: "Focus ring", opts: [["standard", "Standard"], ["bold", "Bold"], ["high-contrast", "High-contrast"]] },
];
const RAIN_DENSITY: [string, string][] = [["0", "Off"], ["0.2", "Low"], ["0.4", "Med"], ["0.6", "High"], ["0.8", "Max"]];
const TOGGLES: { key: keyof UiPrefs; label: string; on: boolean }[] = [
  { key: "high_contrast_text", label: "High-contrast text", on: false },
  { key: "reduce_transparency", label: "Reduce transparency", on: false },
  { key: "zebra_rows", label: "Zebra rows", on: true },
  { key: "link_underlines", label: "Underline links", on: false },
];

// Named looks — each merges a bundle over the current prefs; "Classic" resets everything.
const PRESETS: { name: string; prefs: UiPrefs | null; accent?: string | null }[] = [
  { name: "Classic", prefs: null, accent: null },
  { name: "Focus", prefs: { glow_intensity: "off", scanlines: "off", motion: "calm", density: "comfortable" } },
  { name: "Low-strain", prefs: { high_contrast_text: true, glow_intensity: "subtle", text_scale: "112", rain_density: 0.2 } },
  { name: "OLED", prefs: { background_depth: "pure-black", rain_density: 0, border_glow: "hairline" } },
  { name: "Amber CRT", prefs: { scanlines: "heavy", font_family: "consolas", rain_glyphs: "binary" }, accent: "#ffc24b" },
];

/** Per-user appearance controls — recolors and retunes the whole console (stays dark). */
export function AppearancePanel() {
  const { user, setUser } = useAuth();
  const [msg, setMsg] = useState<string | null>(null);
  if (!user) return null;

  const prefs = user.ui_prefs ?? {};
  const accent = (user.accent_color ?? ACCENT_DEFAULT).toLowerCase();
  const valOf = (key: keyof UiPrefs) => String(prefs[key] ?? DEFAULTS[key] ?? "");

  // Optimistically merge onto the FRESHEST state via a functional update so rapid clicks stack
  // monotonically and out-of-order PATCH responses can't clobber later changes. The AuthContext
  // effects re-apply on the new ui_prefs/accent. On failure, reconcile with the server.
  const reconcile = async () => {
    try {
      setUser(await api.get<User>("/api/auth/me"));
    } catch {
      /* keep optimistic state; next navigation reconciles */
    }
  };

  const savePrefs = async (patch: Partial<UiPrefs>) => {
    setUser((u) => (u ? { ...u, ui_prefs: { ...(u.ui_prefs ?? {}), ...patch } } : u));
    try {
      await api.patch<User>(`/api/users/${user.id}/profile`, { ui_prefs: patch });
      setMsg(null);
    } catch (e) {
      setMsg(e instanceof ApiError ? e.message : "Failed to save");
      await reconcile();
    }
  };

  const chooseAccent = async (hex: string | null) => {
    applyAccent(hex ?? ACCENT_DEFAULT); // instant feedback
    setUser((u) => (u ? { ...u, accent_color: hex } : u));
    try {
      await api.patch<User>(`/api/users/${user.id}/profile`, { accent_color: hex });
      setMsg(null);
    } catch (e) {
      setMsg(e instanceof ApiError ? e.message : "Failed to save");
      await reconcile();
    }
  };

  const applyPreset = async (p: (typeof PRESETS)[number]) => {
    setUser((u) => {
      if (!u) return u;
      const next = p.prefs === null ? null : { ...(u.ui_prefs ?? {}), ...p.prefs };
      return p.accent !== undefined ? { ...u, ui_prefs: next, accent_color: p.accent } : { ...u, ui_prefs: next };
    });
    if (p.accent !== undefined) applyAccent(p.accent ?? ACCENT_DEFAULT);
    try {
      const body: { ui_prefs: UiPrefs | null; accent_color?: string | null } = { ui_prefs: p.prefs };
      if (p.accent !== undefined) body.accent_color = p.accent;
      await api.patch<User>(`/api/users/${user.id}/profile`, body);
      setMsg(null);
    } catch (e) {
      setMsg(e instanceof ApiError ? e.message : "Failed to save");
      await reconcile();
    }
  };

  const segGroup = (segs: Seg[]) =>
    segs.map((s) => (
      <div className="pref-row" key={s.key}>
        <span className="pref-label">{s.label}</span>
        <div className="seg" role="group" aria-label={s.label}>
          {s.opts.map(([value, label]) => (
            <button
              key={value}
              className={`seg-btn ${valOf(s.key) === value ? "active" : ""}`}
              aria-pressed={valOf(s.key) === value}
              onClick={() => savePrefs({ [s.key]: value } as Partial<UiPrefs>)}
            >
              {label}
            </button>
          ))}
        </div>
      </div>
    ));

  return (
    <Panel
      title="// APPEARANCE"
      right={
        user.accent_color || user.ui_prefs ? (
          <button className="link-btn" onClick={() => applyPreset(PRESETS[0])}>
            reset
          </button>
        ) : undefined
      }
    >
      {msg && <div className="banner">ERR: {msg}</div>}
      <p className="muted small" style={{ marginTop: 0 }}>
        Retune the console — it stays dark; only the look changes. Everything applies live and
        saves to your profile.
      </p>

      <div className="pref-section">Presets</div>
      <div className="preset-row">
        {PRESETS.map((p) => (
          <button key={p.name} className="preset-chip" onClick={() => applyPreset(p)}>
            {p.name}
          </button>
        ))}
      </div>

      <div className="pref-section">Color</div>
      <div className="pref-row">
        <span className="pref-label">Accent</span>
        <div className="swatch-row">
          {ACCENTS.map(([name, hex]) => (
            <button
              key={hex}
              className={`swatch ${accent === hex.toLowerCase() ? "active" : ""}`}
              style={{ "--sw": hex } as CSSProperties}
              title={name}
              aria-label={name}
              aria-pressed={accent === hex.toLowerCase()}
              onClick={() => chooseAccent(hex)}
            />
          ))}
          <label className="swatch swatch-custom" title="Custom color" aria-label="Custom color">
            <input
              type="color"
              value={accent}
              onInput={(e) => applyAccent((e.target as HTMLInputElement).value)}
              onChange={(e) => chooseAccent(e.target.value)}
            />
          </label>
        </div>
      </div>
      {segGroup(COLOR)}

      <div className="pref-section">Glow &amp; texture</div>
      {segGroup(TEXTURE)}

      <div className="pref-section">Motion &amp; rain</div>
      <div className="pref-row">
        <span className="pref-label">Rain density</span>
        <div className="seg" role="group" aria-label="Rain density">
          {RAIN_DENSITY.map(([value, label]) => (
            <button
              key={value}
              className={`seg-btn ${String(prefs.rain_density ?? DEFAULT_RAIN_DENSITY) === value ? "active" : ""}`}
              aria-pressed={String(prefs.rain_density ?? DEFAULT_RAIN_DENSITY) === value}
              onClick={() => savePrefs({ rain_density: Number(value) })}
            >
              {label}
            </button>
          ))}
        </div>
      </div>
      {segGroup(MOTION)}

      <div className="pref-section">Typography &amp; density</div>
      {segGroup(TYPO)}

      <div className="pref-section">Accessibility</div>
      {segGroup(A11Y_SEG)}
      {TOGGLES.map((t) => {
        const checked = (prefs[t.key] as boolean | undefined) ?? t.on;
        return (
          <div className="pref-row" key={t.key}>
            <span className="pref-label">{t.label}</span>
            <button
              className={`toggle ${checked ? "on" : ""}`}
              role="switch"
              aria-checked={checked}
              aria-label={t.label}
              onClick={() => savePrefs({ [t.key]: !checked } as Partial<UiPrefs>)}
            >
              <span className="toggle-knob" />
            </button>
          </div>
        );
      })}
    </Panel>
  );
}
