import { useEffect, useRef } from "react";

const KATAKANA = "アカサタナハマヤラワンイキシチニミリ0123456789ABCDEF<>/\\|:.*#=+";
const ASCII = "01<>/[]{}#*+=~:.|";
const F = 16; // cell size, matches --lane-w

/**
 * Full-viewport falling code-rain. Recolored as a telemetry deck: teal "data-pulse"
 * columns + faint lane rules. Throttled to ~24fps, DPR-capped, paused when hidden, and
 * reduced to a single static frame under prefers-reduced-motion.
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
    };

    const frame = () => {
      ctx.fillStyle = "rgba(6,10,8,0.10)"; // trail fade
      ctx.fillRect(0, 0, W, H);
      ctx.font = `${F}px ${mono()}`;
      for (let i = 0; i < cols; i++) {
        const x = i * F;
        if (i % 4 === 0) {
          ctx.strokeStyle = "rgba(25,229,208,0.10)";
          ctx.lineWidth = 1;
          ctx.beginPath();
          ctx.moveTo(x + 0.5, 0);
          ctx.lineTo(x + 0.5, H);
          ctx.stroke();
        }
        const y = drops[i] * F;
        const ch = chars[(Math.random() * chars.length) | 0];
        ctx.shadowBlur = 0;
        ctx.fillStyle = i % 40 === 0 ? "rgba(25,229,208,0.5)" : "rgba(53,255,116,0.35)";
        ctx.fillText(ch, x, y);
        ctx.fillStyle = "#9cffc0"; // bright head
        ctx.shadowColor = "#35ff74";
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
      ctx.fillStyle = "rgba(53,255,116,0.28)";
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

    setup();
    if (reduced) staticFrame();
    else raf = requestAnimationFrame(loop);

    window.addEventListener("resize", onResize);
    document.addEventListener("visibilitychange", onVis);
    return () => {
      running = false;
      cancelAnimationFrame(raf);
      window.removeEventListener("resize", onResize);
      document.removeEventListener("visibilitychange", onVis);
    };
  }, []);

  return <canvas ref={ref} className="rain-canvas" style={{ opacity }} aria-hidden="true" />;
}
