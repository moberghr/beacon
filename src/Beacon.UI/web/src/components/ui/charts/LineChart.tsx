import { useId } from 'react';

export interface LineSeries {
  name: string;
  color: string;
  data: number[];
}

export interface LineChartProps {
  series: LineSeries[];
  height?: number;
  days?: number;
}

/**
 * Multi-series line chart with gridlines, y-axis ticks, and a sparse set
 * of x-axis labels (`d-29`, `d-22`, …). Each series draws a stroke plus
 * a soft gradient area fill. SVG is responsive (`width="100%"`); the
 * `viewBox` fixes the aspect ratio.
 */
export function LineChart({ series, height = 240, days = 30 }: LineChartProps) {
  const uid = useId();
  const w = 1000;
  const h = height;
  const pad = { top: 16, right: 16, bottom: 28, left: 38 };
  const innerW = w - pad.left - pad.right;
  const innerH = h - pad.top - pad.bottom;
  const allMax = Math.max(...series.flatMap((s) => s.data), 1);
  const allMin = 0;
  const range = allMax - allMin || 1;
  const step = innerW / Math.max(days - 1, 1);
  const xAt = (i: number) => pad.left + i * step;
  const yAt = (v: number) => pad.top + innerH - ((v - allMin) / range) * innerH;
  const yTicks = 4;
  const tickValues = Array.from({ length: yTicks + 1 }, (_, i) => Math.round((allMax / yTicks) * i));
  const xLabels = [0, Math.floor(days / 4), Math.floor(days / 2), Math.floor((days * 3) / 4), days - 1].filter(
    (v, i, a) => a.indexOf(v) === i,
  );

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} className="block">
      {tickValues.map((v, i) => (
        <g key={i}>
          <line
            x1={pad.left}
            x2={w - pad.right}
            y1={yAt(v)}
            y2={yAt(v)}
            stroke="var(--border)"
            strokeDasharray={i === 0 ? '' : '2 4'}
          />
          <text
            x={pad.left - 8}
            y={yAt(v) + 3}
            fontSize="10.5"
            fill="var(--text-subtle)"
            fontFamily="var(--font-mono)"
            textAnchor="end"
          >
            {v}
          </text>
        </g>
      ))}
      {xLabels.map((i) => (
        <text
          key={i}
          x={xAt(i)}
          y={h - 8}
          fontSize="10.5"
          fill="var(--text-subtle)"
          fontFamily="var(--font-mono)"
          textAnchor="middle"
        >
          {`d-${days - 1 - i}`}
        </text>
      ))}
      {series.map((s, seriesIndex) => {
        const safeData = s.data.length > 0 ? s.data : Array(days).fill(0);
        const id = `${uid}-lg-${seriesIndex}`;
        const d = safeData.map((v, i) => (i ? 'L' : 'M') + xAt(i) + ' ' + yAt(v)).join(' ');
        const area = d + ` L ${xAt(safeData.length - 1)} ${pad.top + innerH} L ${xAt(0)} ${pad.top + innerH} Z`;
        return (
          <g key={s.name}>
            <defs>
              <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={s.color} stopOpacity="0.18" />
                <stop offset="100%" stopColor={s.color} stopOpacity="0" />
              </linearGradient>
            </defs>
            <path d={area} fill={`url(#${id})`} />
            <path
              d={d}
              stroke={s.color}
              strokeWidth="1.8"
              fill="none"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </g>
        );
      })}
    </svg>
  );
}
