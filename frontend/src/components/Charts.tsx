// Self-contained SVG charts in the terminal palette. Every series is paired with a labelled
// legend so meaning never rides on color alone; numbers use tabular figures.

export const SERIES = ["#35ff74", "#19e5d0", "#ffc24b", "#9085e9", "#ff5a47", "#5e8b7b"];

export function BarChart({ data }: { data: { label: string; value: number }[] }) {
  const max = Math.max(1, ...data.map((d) => d.value));
  return (
    <div
      className="chart-bars"
      role="img"
      aria-label={`Bar chart. ${data.map((d) => `${d.label}: ${d.value}`).join(", ")}`}
    >
      {data.map((d, i) => (
        <div className="bar-row" key={d.label}>
          <span className="bar-label">{d.label}</span>
          <div className="bar-track">
            <div
              className="bar-fill"
              style={{ width: `${(d.value / max) * 100}%`, background: SERIES[i % SERIES.length] }}
            />
          </div>
          <span className="bar-value">{d.value}</span>
        </div>
      ))}
    </div>
  );
}

export function Donut({
  segments,
  caption = "TOTAL",
}: {
  segments: { label: string; value: number; color: string }[];
  caption?: string;
}) {
  const total = segments.reduce((a, s) => a + s.value, 0) || 1;
  const R = 42;
  const CIRC = 2 * Math.PI * R;
  let offset = 0;
  return (
    <div className="chart-donut">
      <svg
        viewBox="0 0 100 100"
        className="donut-svg"
        role="img"
        aria-label={`${caption}: ${total}. ${segments.map((s) => `${s.label} ${s.value}`).join(", ")}`}
      >
        <circle cx="50" cy="50" r={R} fill="none" stroke="var(--border)" strokeWidth="11" />
        {segments.map((s, i) => {
          const len = (s.value / total) * CIRC;
          const el = (
            <circle
              key={i}
              cx="50"
              cy="50"
              r={R}
              fill="none"
              stroke={s.color}
              strokeWidth="11"
              strokeDasharray={`${len} ${CIRC - len}`}
              strokeDashoffset={-offset}
              transform="rotate(-90 50 50)"
            />
          );
          offset += len;
          return el;
        })}
        <text x="50" y="49" textAnchor="middle" className="donut-total">
          {total}
        </text>
        <text x="50" y="61" textAnchor="middle" className="donut-cap">
          {caption}
        </text>
      </svg>
      <div className="chart-legend">
        {segments.map((s) => (
          <div className="legend-row" key={s.label}>
            <span className="legend-chip" style={{ background: s.color }} />
            <span className="legend-label">{s.label}</span>
            <span className="legend-val">{s.value}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

export function AreaChart({
  points,
  color = "#35ff74",
}: {
  points: { label: string; value: number }[];
  color?: string;
}) {
  const W = 300;
  const H = 96;
  const P = 8;
  const n = points.length;
  const max = Math.max(1, ...points.map((p) => p.value));
  const x = (i: number) => P + (i / Math.max(1, n - 1)) * (W - 2 * P);
  const y = (v: number) => H - P - (v / max) * (H - 2 * P);
  const line = points.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)} ${y(p.value).toFixed(1)}`).join(" ");
  const area = `${line} L${x(n - 1).toFixed(1)} ${H - P} L${x(0).toFixed(1)} ${H - P} Z`;
  return (
    <div>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="area-svg"
        role="img"
        aria-label={`Trend, ${points[0]?.label} to ${points[n - 1]?.label}, peak ${max}.`}
      >
        <path d={area} fill={color} opacity="0.12" />
        <path d={line} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
        <circle cx={x(n - 1)} cy={y(points[n - 1].value)} r="3.2" fill={color} />
      </svg>
      <div className="area-axis">
        <span>{points[0]?.label}</span>
        <span className="area-peak">peak {max}</span>
        <span>{points[n - 1]?.label}</span>
      </div>
    </div>
  );
}

export function SegmentBar({
  segments,
}: {
  segments: { label: string; value: number; color: string; glyph: string }[];
}) {
  const total = segments.reduce((a, s) => a + s.value, 0) || 1;
  return (
    <div>
      <div
        className="segbar"
        role="img"
        aria-label={segments.map((s) => `${s.label} ${s.value}`).join(", ")}
      >
        {segments
          .filter((s) => s.value > 0)
          .map((s) => (
            <div
              key={s.label}
              className="segbar-seg"
              style={{ width: `${(s.value / total) * 100}%`, background: s.color }}
              title={`${s.label}: ${s.value}`}
            />
          ))}
      </div>
      <div className="chart-legend wrap">
        {segments.map((s) => (
          <div className="legend-row" key={s.label}>
            <span className="legend-glyph" style={{ color: s.color }}>
              {s.glyph}
            </span>
            <span className="legend-label">{s.label}</span>
            <span className="legend-val">{s.value}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
