import { type CSSProperties, useEffect, useRef } from "react";

const KATAKANA = "アカサタナハマヤラワンイキシチニミリ0123456789ABCDEF<>/\\|:.*#=+";
const ASCII = "01<>/[]{}#*+=~:.|";
const F = 16; // cell size, matches --lane-w
const FALLBACK: [number, number, number] = [53, 255, 116];

/**
 * Full-viewport falling code-rain. Follows the live accent color (--accent-rgb), so the
 * telemetry deck recolors with each user's theme. Throttled to ~24fps, DPR-capped, paused
 * when hidden, and reduced to a single static frame under prefers-reduced-motion.
 */
export function MatrixRain({ opacity = 0.4 }: { opacity?: number }) {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext("2d");
    if (!canvas || !ctx) return;

    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    let W = 0;
    let H = 0;
    let cols = 0;
    let drops: number[] = [];
    let chars = KATAKANA;
    let raf = 0;
    let last = 0;
    let running = true;

    // Live accent — re-read whenever the theme variable changes.
    let charColor = "rgba(53,255,116,0.5)";
    let laneColor = "rgba(53,255,116,0.06)";
    let headColor = "#cfffe0";
    let glowColor = "rgb(53,255,116)";
    const readAccent = () => {
      const raw = getComputedStyle(document.documentElement).getPropertyValue("--accent-rgb").trim();
      const parts = raw.split(/[\s,]+/).map(Number).filter((n) => !Number.isNaN(n));
      const [r, g, b] = parts.length === 3 ? (parts as [number, number, number]) : FALLBACK;
      charColor = `rgba(${r},${g},${b},0.5)`;
      laneColor = `rgba(${r},${g},${b},0.06)`;
      glowColor = `rgb(${r},${g},${b})`;
      // Bright head: accent lightened toward white so the leading glyph reads hot.
      const lift = (c: number) => Math.round((c + 255 * 2) / 3);
      headColor = `rgb(${lift(r)},${lift(g)},${lift(b)})`;
    };

    const mono = () =>
      getComputedStyle(document.body).getPropertyValue("--font-mono") || "monospace";

    const setup = () => {
      W = window.innerWidth;
      H = window.innerHeight;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.floor(W * dpr);
      canvas.height = Math.floor(H * dpr);
      canvas.style.width = `${W}px`;
      canvas.style.height = `${H}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.font = `${F}px ${mono()}`;
      chars = ctx.measureText("ア").width > 2 ? KATAKANA : ASCII;
      cols = Math.max(1, Math.floor(W / F));
      drops = Array.from({ length: cols }, () => (Math.random() * -H) / F);
      readAccent();
    };

    const frame = () => {
      ctx.fillStyle = "rgba(6,10,8,0.10)"; // trail fade toward the deck background
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
        ctx.shadowBlur = 8;
        ctx.fillText(ch, x, y);
        ctx.shadowBlur = 0;
        if (y > H && Math.random() > 0.975) drops[i] = 0;
        else drops[i] += 0.5;
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
      if (!running) return;
      if (t - last >= 42) {
        frame();
        last = t;
      }
      raf = requestAnimationFrame(loop);
    };

    const onResize = () => {
      setup();
      if (reduced) staticFrame();
    };
    const onVis = () => {
      running = !document.hidden;
      if (running && !reduced) {
        last = 0;
        raf = requestAnimationFrame(loop);
      }
    };
    // Recolor immediately when the accent theme variable changes.
    const themeObserver = new MutationObserver(() => {
      readAccent();
      if (reduced) staticFrame();
    });
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["style", "data-calm"],
    });

    setup();
    if (reduced) staticFrame();
    else raf = requestAnimationFrame(loop);

    window.addEventListener("resize", onResize);
    document.addEventListener("visibilitychange", onVis);
    return () => {
      running = false;
      cancelAnimationFrame(raf);
      themeObserver.disconnect();
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
