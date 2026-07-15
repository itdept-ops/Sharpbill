// UI theming applied to the document root as CSS variables + data-attributes.
// --accent-rgb drives every "green"; applyUiPrefs() resolves the per-user customization bag
// (base tone, glow, motion, rain, density, typography, accessibility) and writes it as inline
// vars/attributes on <html>. Every axis defaults to today's look, so a null bag changes nothing.

import type { UiPrefs } from "../types";

const DEFAULT_ACCENT = "53 255 116"; // the default green (space-separated RGB)

/** Console code-rain opacity when the user hasn't chosen a density (matches the "Low" segment). */
export const DEFAULT_RAIN_DENSITY = 0.2;

export function hexToRgbTriple(hex: string): string | null {
  const m = /^#?([0-9a-fA-F]{6})$/.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return `${(n >> 16) & 255} ${(n >> 8) & 255} ${n & 255}`;
}

function relLuminance(r: number, g: number, b: number): number {
  const lin = [r, g, b].map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
}

/** Set the per-user accent color (hex); null/invalid reverts to the default green. */
export function applyAccent(hex: string | null | undefined): void {
  const triple = hex ? hexToRgbTriple(hex) : null;
  const style = document.documentElement.style;
  const resolved = triple ?? DEFAULT_ACCENT;
  style.setProperty("--accent-rgb", resolved);
  // Keep on-accent text (button labels, active segments, toggle knobs) legible for ANY accent:
  // dark ink on a light accent, light ink on a dark one.
  const [r, g, b] = resolved.split(" ").map(Number);
  style.setProperty("--green-ink", relLuminance(r, g, b) > 0.35 ? "#041008" : "#f2fff7");
}

/** Toggle the global (admin) calm mode. Composes with per-user motion via the CSS floor. */
export function applyCalm(calm: boolean): void {
  document.documentElement.dataset.calm = calm ? "true" : "false";
}

// ---- Per-user preference resolution ----------------------------------------------------

interface Ramp {
  bg: string;
  bg2: string;
  panel: string;
  panel2: string;
  border: string;
}

// Curated dark-only surface ramps. "abyss" reproduces today's exact values.
const TONES: Record<string, Ramp> = {
  abyss: { bg: "#060a08", bg2: "#0a120e", panel: "#0c140f", panel2: "#10201a", border: "#17322a" },
  ink: { bg: "#06080f", bg2: "#0a0e18", panel: "#0c1018", panel2: "#121826", border: "#1e2740" },
  graphite: { bg: "#0a0a0b", bg2: "#100f12", panel: "#131316", panel2: "#1c1b20", border: "#2c2b32" },
  midnight: { bg: "#070611", bg2: "#0d0b1c", panel: "#100e20", panel2: "#191634", border: "#28234b" },
  "warm-black": {
    bg: "#0b0906",
    bg2: "#15100a",
    panel: "#17120c",
    panel2: "#221a12",
    border: "#3a2f22",
  },
};

// System font stacks only — CSP-safe, no @font-face/CDN. Each ends in the generic `monospace`.
const FONT_STACKS: Record<string, string> = {
  system:
    'ui-monospace, "Cascadia Mono", "Cascadia Code", "JetBrains Mono", "SF Mono", ' +
    '"Segoe UI Mono", "Roboto Mono", Menlo, Consolas, "Liberation Mono", monospace',
  "high-legibility":
    '"JetBrains Mono", "IBM Plex Mono", "DejaVu Sans Mono", "Liberation Mono", Consolas, ' +
    "ui-monospace, monospace",
  cascadia: '"Cascadia Code", "Cascadia Mono", ui-monospace, monospace',
  jetbrains: '"JetBrains Mono", "Roboto Mono", ui-monospace, monospace',
  consolas: 'Consolas, "Liberation Mono", "DejaVu Sans Mono", ui-monospace, monospace',
  menlo: 'Menlo, "SF Mono", "Roboto Mono", ui-monospace, monospace',
};

const GLOW: Record<string, string> = { off: "0", subtle: "0.5", normal: "1", intense: "1.6" };
const DENSITY: Record<string, string> = { compact: "0.82", comfortable: "1", spacious: "1.18" };
const RADIUS: Record<string, string> = { sharp: "2px", soft: "5px", round: "9px" };
const FOCUS: Record<string, string> = {
  standard: "0 0 0 3px rgb(var(--accent-rgb) / 0.18)",
  bold: "0 0 0 4px rgb(var(--accent-rgb) / 0.4)",
  "high-contrast": "0 0 0 2px #05100a, 0 0 0 5px #ffffff",
};
const HC = { ink: "#f2fff7", inkDim: "#d3ece0", muted: "#8fbaa9" };

function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function rgbToHex(r: number, g: number, b: number): string {
  return (
    "#" +
    [r, g, b].map((c) => Math.max(0, Math.min(255, Math.round(c))).toString(16).padStart(2, "0")).join("")
  );
}
function mix(a: string, b: string, t: number): string {
  const [ar, ag, ab] = hexToRgb(a);
  const [br, bg, bb] = hexToRgb(b);
  return rgbToHex(ar + (br - ar) * t, ag + (bg - ag) * t, ab + (bb - ab) * t);
}

// Background depth composes ON the chosen tone: darker toward pure black, or slightly lifted.
function withDepth(tone: Ramp, depth: UiPrefs["background_depth"]): Ramp {
  if (depth === "pure-black") {
    return {
      bg: mix(tone.bg, "#000000", 0.72),
      bg2: mix(tone.bg2, "#000000", 0.6),
      panel: mix(tone.panel, "#000000", 0.5),
      panel2: mix(tone.panel2, "#000000", 0.42),
      border: mix(tone.border, "#000000", 0.3),
    };
  }
  if (depth === "elevated") {
    return {
      bg: tone.bg,
      bg2: tone.bg2,
      panel: mix(tone.panel, "#ffffff", 0.05),
      panel2: mix(tone.panel2, "#ffffff", 0.07),
      border: mix(tone.border, "#ffffff", 0.06),
    };
  }
  return tone;
}

const root = () => document.documentElement;
function setOrClear(key: string, value: string | null): void {
  if (value) root().style.setProperty(key, value);
  else root().style.removeProperty(key);
}
function attr(name: string, value: string | null): void {
  const ds = root().dataset;
  if (value) ds[name] = value;
  else delete ds[name];
}

/**
 * Resolve and apply the per-user UI preference bag. Each axis is written as an inline CSS
 * variable or a data-attribute on <html>; a missing key clears its override so it falls back
 * to the stylesheet default (today's look). Passing null reverts every axis.
 */
export function applyUiPrefs(prefs: UiPrefs | null | undefined): void {
  // Color ramp — base tone × depth. Only override when the user picked a non-default; else the
  // :root values (which equal "abyss / standard") shine through.
  if (prefs?.base_tone || prefs?.background_depth || prefs?.border_glow) {
    const ramp = withDepth(TONES[prefs.base_tone ?? "abyss"] ?? TONES.abyss, prefs.background_depth);
    // Border style composes on the tone's border: fade toward the background (hairline) or tint
    // toward the accent (neon). "standard"/unset keeps the tone's own border.
    let border = ramp.border;
    if (prefs.border_glow === "hairline") border = mix(ramp.border, ramp.bg, 0.5);
    else if (prefs.border_glow === "neon") border = "rgb(var(--accent-rgb) / 0.5)";
    setOrClear("--bg", ramp.bg);
    setOrClear("--bg-2", ramp.bg2);
    setOrClear("--panel", ramp.panel);
    setOrClear("--panel-2", ramp.panel2);
    setOrClear("--border", border);
    setOrClear("--bg-rgb", hexToRgbTriple(ramp.bg) ?? "6 10 8");
  } else {
    ["--bg", "--bg-2", "--panel", "--panel-2", "--border", "--bg-rgb"].forEach((k) =>
      setOrClear(k, null),
    );
  }

  // High-contrast text ramp — lifts text toward white, never touches backgrounds (stays dark).
  // Elevated panels are lighter, so --muted is lifted there too to keep secondary labels ≥ AA.
  const hc = prefs?.high_contrast_text;
  const elevated = prefs?.background_depth === "elevated";
  setOrClear("--ink", hc ? HC.ink : null);
  setOrClear("--ink-dim", hc ? HC.inkDim : null);
  setOrClear("--muted", hc ? HC.muted : elevated ? "#7ba593" : null);

  // Single-value variable swaps.
  setOrClear("--glow-scale", prefs?.glow_intensity ? GLOW[prefs.glow_intensity] : null);
  setOrClear("--density", prefs?.density ? DENSITY[prefs.density] : null);
  setOrClear("--font-scale", prefs?.text_scale ? String(Number(prefs.text_scale) / 100) : null);
  setOrClear("--radius", prefs?.corner_radius ? RADIUS[prefs.corner_radius] : null);
  setOrClear("--font-mono", prefs?.font_family ? FONT_STACKS[prefs.font_family] : null);
  setOrClear("--focus-ring", prefs?.focus_ring ? FOCUS[prefs.focus_ring] : null);
  setOrClear("--zebra", prefs?.zebra_rows === false ? "transparent" : null);

  // Behavioral attributes (consumed by CSS selectors and by MatrixRain). "full"/"standard"/
  // "normal"/"katakana" are the defaults, so they clear the attribute entirely.
  attr("motion", prefs?.motion && prefs.motion !== "full" ? prefs.motion : null);
  attr("scanlines", prefs?.scanlines && prefs.scanlines !== "standard" ? prefs.scanlines : null);
  attr("rainSpeed", prefs?.rain_speed && prefs.rain_speed !== "normal" ? prefs.rain_speed : null);
  attr("rainGlyphs", prefs?.rain_glyphs && prefs.rain_glyphs !== "katakana" ? prefs.rain_glyphs : null);
  attr("underline", prefs?.link_underlines ? "on" : null);
  attr("solid", prefs?.reduce_transparency ? "on" : null);
}
