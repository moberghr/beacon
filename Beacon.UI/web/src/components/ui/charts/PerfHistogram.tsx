export type PerfMetric = 'avg' | 'p50' | 'p95' | 'p99';

export interface PerfBucket {
  label: string;
  avgMs: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
}

export interface PerfHistogramProps {
  buckets: PerfBucket[];
  metric?: PerfMetric;
  height?: number;
}

/**
 * Bar chart of query-execution latency per bucket. The selected metric
 * (`avg`/`p50`/`p95`/`p99`) drives the bar height; a dashed warning
 * line marks ~65% of the displayed max. Renders an empty-state message
 * when there are no buckets.
 */
export function PerfHistogram({ buckets, metric = 'avg', height = 200 }: PerfHistogramProps) {
  if (!buckets || buckets.length === 0) {
    return (
      <div className="flex items-center justify-center text-sm text-text-muted" style={{ height }}>
        No execution data yet
      </div>
    );
  }
  const w = 1000;
  const h = height;
  const pad = { top: 14, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;

  const getValue = (b: PerfBucket) => {
    if (metric === 'p50') return b.p50Ms;
    if (metric === 'p95') return b.p95Ms;
    if (metric === 'p99') return b.p99Ms;
    return b.avgMs;
  };

  const values = buckets.map(getValue);
  const max = Math.max(...values, 1);
  const bw = innerW / buckets.length;
  const p95Line = max * 0.65;

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} className="block">
      {[0, 0.25, 0.5, 0.75, 1].map((t, i) => (
        <g key={i}>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={pad.top + innerH * (1 - t)}
            y2={pad.top + innerH * (1 - t)}
            stroke="var(--border)"
            strokeDasharray={t === 0 ? '' : '2 4'}
          />
          <text
            x={pad.left - 8}
            y={pad.top + innerH * (1 - t) + 3}
            fontSize="10.5"
            fill="var(--text-subtle)"
            fontFamily="var(--font-mono)"
            textAnchor="end"
          >
            {Math.round(max * t)}ms
          </text>
        </g>
      ))}
      {buckets.map((b, i) => {
        const val = getValue(b);
        const bh = (val / max) * innerH;
        const x = pad.left + i * bw + 2;
        const y = pad.top + innerH - bh;
        const color =
          b.p99Ms > 0 && val === b.p99Ms && metric === 'p99'
            ? 'var(--crit)'
            : b.p95Ms > 0 && val === b.p95Ms && metric === 'p95'
              ? 'var(--warn)'
              : 'var(--brand-500)';
        return (
          <g key={i}>
            <rect x={x} y={y} width={bw - 4} height={bh} rx="2" fill={color} opacity="0.85" />
            {i % Math.max(1, Math.floor(buckets.length / 10)) === 0 && (
              <text
                x={x + (bw - 4) / 2}
                y={h - 10}
                fontSize="10.5"
                fill="var(--text-subtle)"
                fontFamily="var(--font-mono)"
                textAnchor="middle"
              >
                {b.label}
              </text>
            )}
          </g>
        );
      })}
      {max > 0 && (
        <>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={pad.top + innerH * (1 - p95Line / max)}
            y2={pad.top + innerH * (1 - p95Line / max)}
            stroke="var(--warn)"
            strokeDasharray="3 3"
            opacity="0.7"
          />
          <text
            x={w - pad.right - 4}
            y={pad.top + innerH * (1 - p95Line / max) - 4}
            fontSize="10.5"
            fill="var(--warn)"
            fontFamily="var(--font-mono)"
            textAnchor="end"
          >
            p95 · {Math.round(p95Line)}ms
          </text>
        </>
      )}
    </svg>
  );
}
