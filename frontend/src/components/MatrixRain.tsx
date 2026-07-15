import { type CSSProperties, useEffect, useRef } from "react";

const KATAKANA = "アカサタナハマヤラワンイキシチニミリ0123456789ABCDEF<>/\\|:.*#=+";
const ASCII = "01<>/[]{}#*+=~:.|";
const GLYPH_SETS: Record<string, string> = {
  katakana: KATAKANA,
  ascii: ASCII,
  binary: "01",
  hex: "0123456789ABCDEF",
};
const SPEED_MS: Record<string, number> = { slow: 70, normal: 42, fast: 28 };
const SPEED_STEP: Record<string, number> = { slow: 0.4, normal: 0.5, fast: 0.72 };
const F = 16; // cell size, matches --lane-w
const FALLBACK: [number, number, number] = [53, 255, 116];

/**
 * Full-viewport falling code-rain, driven entirely by live theme state so it retunes without
 * remounting: accent color (--accent-rgb), base tone (--bg-rgb trail-fade), glow (--glow-scale),
 * and the per-user rain axes (data-rain-speed, data-rain-glyphs). Falls back to a single static
 * frame under prefers-reduced-motion, motion="reduced", or rain speed "still". DPR-capped,
 * throttled, paused when the tab is hidden. `opacity` (rain density) feeds --rain-base.
 */
export function MatrixRain({ opacity = 0.4 }: { opacity?: number }) {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext("2d");
    if (!canvas || !ctx) return;
    const osReduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    let W = 0;
    let H = 0;
    let cols = 0;
    let drops: number[] = [];
    let raf = 0;
    let last = 0;
    let visible = true;

    // Live theme state, refreshed by readTheme() on setup and on any documentElement change.
    let chars = KATAKANA;
    let charColor = "rgba(53,255,116,0.5)";
    let laneColor = "rgba(53,255,116,0.06)";
    let headColor = "#cfffe0";
    let glowColor = "rgb(53,255,116)";
    let trail = "rgba(6,10,8,0.1)";
    let glowBlur = 8;
    let stepPx = 0.5;
    let throttle = 42;
    let isStatic = false;

    const mono = () =>
      getComputedStyle(document.body).getPropertyValue("--font-mono") || "monospace";

    const parseTriple = (raw: string, fb: [number, number, number]): [number, number, number] => {
      const p = raw.split(/[\s,]+/).map(Number).filter((n) => !Number.isNaN(n));
      return p.length === 3 ? (p as [number, number, number]) : fb;
    };

    const readTheme = () => {
      const cs = getComputedStyle(document.documentElement);
      const ds = document.documentElement.dataset;
      const [r, g, b] = parseTriple(cs.getPropertyValue("--accent-rgb"), FALLBACK);
      charColor = `rgba(${r},${g},${b},0.5)`;
      laneColor = `rgba(${r},${g},${b},0.06)`;
      glowColor = `rgb(${r},${g},${b})`;
      const lift = (c: number) => Math.round((c + 255 * 2) / 3); // accent → bright head
      headColor = `rgb(${lift(r)},${lift(g)},${lift(b)})`;
      const [br, bg, bb] = parseTriple(cs.getPropertyValue("--bg-rgb"), [6, 10, 8]);
      trail = `rgba(${br},${bg},${bb},0.1)`; // fade toward the live base tone
      // Guard on NaN (not truthiness) so glow "off" (--glow-scale: 0) flattens the head glyph.
      const gs = parseFloat(cs.getPropertyValue("--glow-scale"));
      glowBlur = 8 * (Number.isFinite(gs) ? gs : 1);

      const gk = ds.rainGlyphs || "katakana";
      ctx.font = `${F}px ${mono()}`;
      const hasKatakana = ctx.measureText("ア").width > 2;
      chars = gk === "katakana" ? (hasKatakana ? KATAKANA : ASCII) : GLYPH_SETS[gk] || KATAKANA;

      const speed = ds.rainSpeed || "normal";
      isStatic = osReduced || ds.motion === "reduced" || speed === "still";
      throttle = SPEED_MS[speed] ?? 42;
      stepPx = SPEED_STEP[speed] ?? 0.5;
    };

    const setup = () => {
      W = window.innerWidth;
      H = window.innerHeight;
      // Never collapse the canvas to 0×0. This happens when the component first paints before
      // layout (a background tab, a session-restored tab, bfcache) where innerWidth reads 0;
      // the ResizeObserver below re-runs setup the moment a real size is available.
      if (W === 0 || H === 0) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.floor(W * dpr);
      canvas.height = Math.floor(H * dpr);
      canvas.style.width = `${W}px`;
      canvas.style.height = `${H}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      cols = Math.max(1, Math.floor(W / F));
      drops = Array.from({ length: cols }, () => (Math.random() * -H) / F);
      readTheme();
    };

    const frame = () => {
      ctx.fillStyle = trail;
      ctx.fillRect(0, 0, W, H);
      ctx.font = `${F}px ${mono()}`;
      for (let i = 0; i < cols; i++) {
        const x = i * F;
        if (i % 4 === 0) {
          ctx.strokeStyle = laneColor;
          ctx.lineWidth = 1;
          ctx.beginPath();
          ctx.moveTo(x + 0.5, 0);
          ctx.lineTo(x + 0.5, H);
          ctx.stroke();
        }
        const y = drops[i] * F;
        const ch = chars[(Math.random() * chars.length) | 0];
        ctx.shadowBlur = 0;
        ctx.fillStyle = charColor;
        ctx.fillText(ch, x, y);
        ctx.fillStyle = headColor; // bright leading glyph
        ctx.shadowColor = glowColor;
        ctx.shadowBlur = glowBlur;
        ctx.fillText(ch, x, y);
        ctx.shadowBlur = 0;
        if (y > H && Math.random() > 0.975) drops[i] = 0;
        else drops[i] += stepPx;
      }
    };

    const staticFrame = () => {
      ctx.clearRect(0, 0, W, H);
      ctx.font = `${F}px ${mono()}`;
      ctx.fillStyle = charColor;
      const rows = Math.floor(H / F);
      for (let i = 0; i < cols; i++) {
        for (let j = 0; j < rows; j += 3) {
          ctx.fillText(chars[(Math.random() * chars.length) | 0], i * F, j * F);
        }
      }
    };

    const loop = (t: number) => {
      if (!visible || isStatic) return;
      if (t - last >= throttle) {
        frame();
        last = t;
      }
      raf = requestAnimationFrame(loop);
    };

    // (Re)start in the mode the current theme dictates: a single static frame, or the loop.
    const start = () => {
      cancelAnimationFrame(raf);
      if (isStatic) staticFrame();
      else if (visible) {
        last = 0;
        raf = requestAnimationFrame(loop);
      }
    };

    const onResize = () => {
      setup();
      start();
    };
    const onVis = () => {
      visible = !document.hidden;
      if (visible) setup(); // a tab that first painted while hidden may have measured 0×0 — re-measure
      start();
    };
    // Any accent/tone/glow (inline style) or motion/speed/glyph (data-attr) change retunes live.
    const observer = new MutationObserver(() => {
      readTheme();
      start();
    });
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["style", "data-calm", "data-motion", "data-rain-speed", "data-rain-glyphs"],
    });

    // A ResizeObserver on the viewport root catches the initial 0 -> real size transition when the
    // component first paints while the tab is hidden/laid out late (a plain window "resize" never
    // fires in that case), plus ordinary viewport resizes.
    const ro = new ResizeObserver(() => onResize());
    ro.observe(document.documentElement);

    setup();
    start();
    window.addEventListener("resize", onResize);
    document.addEventListener("visibilitychange", onVis);
    return () => {
      visible = false;
      cancelAnimationFrame(raf);
      observer.disconnect();
      ro.disconnect();
      window.removeEventListener("resize", onResize);
      document.removeEventListener("visibilitychange", onVis);
    };
  }, []);

  return (
    <canvas
      ref={ref}
      className="rain-canvas"
      style={{ "--rain-base": opacity } as CSSProperties}
      aria-hidden="true"
    />
  );
}
