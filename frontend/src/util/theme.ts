// UI theming applied to the document root as CSS variables.
// --accent-rgb drives every "green" in the stylesheet; data-calm gates the code-rain/scanlines.

const DEFAULT_ACCENT = "53 255 116"; // the default green (space-separated RGB)

export function hexToRgbTriple(hex: string): string | null {
  const m = /^#?([0-9a-fA-F]{6})$/.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return `${(n >> 16) & 255} ${(n >> 8) & 255} ${n & 255}`;
}

/** Set the per-user accent color (hex); null/invalid reverts to the default green. */
export function applyAccent(hex: string | null | undefined): void {
  const triple = hex ? hexToRgbTriple(hex) : null;
  document.documentElement.style.setProperty("--accent-rgb", triple ?? DEFAULT_ACCENT);
}

/** Toggle the global calm (reduced-motion) mode. */
export function applyCalm(calm: boolean): void {
  document.documentElement.dataset.calm = calm ? "true" : "false";
}
