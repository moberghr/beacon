// Tiny SVG sparkline / line / bar helpers
function Sparkline({ points, color = "var(--brand-500)", fill = true, width = 88, height = 28 }) {
  const max = Math.max(...points, 1);
  const min = Math.min(...points, 0);
  const range = max - min || 1;
  const step = points.length > 1 ? width / (points.length - 1) : width;
  const coords = points.map((v, i) => [i * step, height - ((v - min) / range) * (height - 4) - 2]);
  const d = coords.map((c, i) => (i ? "L" : "M") + c[0].toFixed(1) + " " + c[1].toFixed(1)).join(" ");
  const area = d + ` L ${width} ${height} L 0 ${height} Z`;
  const id = "sg" + Math.random().toString(36).slice(2, 8);
  return (
    <svg className="kpi__spark" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" width={width} height={height}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.18" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      {fill && <path d={area} fill={`url(#${id})`} />}
      <path d={d} fill="none" stroke={color} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function LineChart({ series, height = 240, days = 30, annotate = true }) {
  // series: [{ name, color, data: number[] }, ...]
  const w = 1000, h = height;
  const pad = { top: 16, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;
  const allMax = Math.max(...series.flatMap((s) => s.data), 1);
  const allMin = 0;
  const range = allMax - allMin || 1;
  const step = innerW / (days - 1);
  const xAt = (i) => pad.left + i * step;
  const yAt = (v) => pad.top + innerH - ((v - allMin) / range) * innerH;

  const yTicks = 4;
  const tickValues = Array.from({ length: yTicks + 1 }, (_, i) => Math.round((allMax / yTicks) * i));
  const xLabels = [0, 7, 14, 21, days - 1];

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} style={{ display: "block" }}>
      {/* horizontal grid */}
      {tickValues.map((v, i) => (
        <g key={i}>
          <line
            x1={pad.left} x2={w - pad.right}
            y1={yAt(v)} y2={yAt(v)}
            stroke="var(--border)" strokeDasharray={i === 0 ? "" : "2 4"}
          />
          <text x={pad.left - 8} y={yAt(v) + 3} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="end">
            {v}
          </text>
        </g>
      ))}
      {/* x labels */}
      {xLabels.map((i) => (
        <text key={i} x={xAt(i)} y={h - 8} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="middle">
          {`d-${days - 1 - i}`}
        </text>
      ))}
      {/* series */}
      {series.map((s) => {
        const id = "lg-" + s.name.replace(/\s/g, "");
        const d = s.data.map((v, i) => (i ? "L" : "M") + xAt(i) + " " + yAt(v)).join(" ");
        const area = d + ` L ${xAt(s.data.length - 1)} ${pad.top + innerH} L ${xAt(0)} ${pad.top + innerH} Z`;
        return (
          <g key={s.name}>
            <defs>
              <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={s.color} stopOpacity="0.18" />
                <stop offset="100%" stopColor={s.color} stopOpacity="0" />
              </linearGradient>
            </defs>
            <path d={area} fill={`url(#${id})`} />
            <path d={d} stroke={s.color} strokeWidth="1.8" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </g>
        );
      })}
      {/* annotation: peak on first series */}
      {annotate && series[0] && (() => {
        const idx = series[0].data.indexOf(Math.max(...series[0].data));
        const x = xAt(idx), y = yAt(series[0].data[idx]);
        return (
          <g>
            <circle cx={x} cy={y} r="4" fill="var(--surface)" stroke={series[0].color} strokeWidth="2" />
            <line x1={x} x2={x} y1={y - 8} y2={pad.top - 4} stroke="var(--border-strong)" strokeDasharray="2 3" />
            <rect x={x - 32} y={pad.top - 18} width="64" height="14" rx="3" fill="var(--surface)" stroke="var(--border-strong)" />
            <text x={x} y={pad.top - 8} fontSize="10.5" fill="var(--text)" fontFamily="var(--font-mono)" textAnchor="middle">
              peak {series[0].data[idx]}
            </text>
          </g>
        );
      })()}
    </svg>
  );
}

function PerfHistogram({ buckets, height = 200 }) {
  // buckets: [{ label, value, p?: 'p50'|'p95'|'p99' }]
  const w = 1000, h = height;
  const pad = { top: 14, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;
  const max = Math.max(...buckets.map((b) => b.value), 1);
  const bw = innerW / buckets.length;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} style={{ display: "block" }}>
      {[0, 0.25, 0.5, 0.75, 1].map((t, i) => (
        <g key={i}>
          <line x1={pad.left} x2={w - pad.right} y1={pad.top + innerH * (1 - t)} y2={pad.top + innerH * (1 - t)} stroke="var(--border)" strokeDasharray={t === 0 ? "" : "2 4"} />
          <text x={pad.left - 8} y={pad.top + innerH * (1 - t) + 3} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="end">{Math.round(max * t)}ms</text>
        </g>
      ))}
      {buckets.map((b, i) => {
        const bh = (b.value / max) * innerH;
        const x = pad.left + i * bw + 2;
        const y = pad.top + innerH - bh;
        const color = b.p === "p99" ? "var(--crit)" : b.p === "p95" ? "var(--warn)" : "var(--brand-500)";
        return (
          <g key={i}>
            <rect x={x} y={y} width={bw - 4} height={bh} rx="2" fill={color} opacity="0.85" />
            {i % 3 === 0 && (
              <text x={x + (bw - 4) / 2} y={h - 10} fontSize="10.5" fill="var(--text-subtle)" fontFamily="var(--font-mono)" textAnchor="middle">{b.label}</text>
            )}
          </g>
        );
      })}
      {/* p95 line */}
      <line x1={pad.left} x2={w - pad.right} y1={pad.top + innerH * 0.35} y2={pad.top + innerH * 0.35} stroke="var(--warn)" strokeDasharray="3 3" opacity="0.7" />
      <text x={w - pad.right - 4} y={pad.top + innerH * 0.35 - 4} fontSize="10.5" fill="var(--warn)" fontFamily="var(--font-mono)" textAnchor="end">p95 · 320ms</text>
    </svg>
  );
}

window.Sparkline = Sparkline;
window.LineChart = LineChart;
window.PerfHistogram = PerfHistogram;
