import { Icon } from '@/components/Icon';

interface Point {
  sampledAt: string;
  resultCount: number;
}

/**
 * Result-count progression — line chart over recent executions.
 * Renders an empty-state message if there are fewer than 2 samples.
 */
export function TaskResultChart({ points }: { points: Point[] }) {
  return (
    <div className="card">
      <div className="card__head">
        <Icon.Activity size={15} className="muted" />
        <h3 className="card__title">Result count progression</h3>
        <span className="card__sub">per execution</span>
      </div>
      <div className="chart-wrap">
        {points.length < 2
          ? <ChartEmpty />
          : <Chart points={points} />}
        <div className="legend" style={{ marginTop: 4, paddingLeft: 4 }}>
          <span className="legend__item">
            <span className="legend__sw" style={{ background: 'var(--brand-500)' }} />Result count
          </span>
          <span className="legend__item">
            <span className="legend__sw" style={{ background: 'var(--warn)', opacity: 0.5 }} />Threshold
          </span>
          <span className="muted" style={{ marginLeft: 'auto', fontSize: 11.5, fontFamily: 'var(--font-mono)' }}>
            {points.length} sample{points.length === 1 ? '' : 's'} · alerts when count &gt; 0
          </span>
        </div>
      </div>
    </div>
  );
}

function ChartEmpty() {
  return (
    <div className="muted" style={{ textAlign: 'center', padding: '40px 12px', fontSize: 12.5 }}>
      Not enough samples yet — chart shows once two or more executions are recorded.
    </div>
  );
}

function Chart({ points }: { points: Point[] }) {
  const w = 720;
  const h = 200;
  const left = 40;
  const right = w - 20;
  const top = 50;
  const bottom = 170;
  const innerW = right - left;
  const maxValue = Math.max(1, ...points.map(p => p.resultCount));
  const yFor = (v: number) => bottom - (v / maxValue) * (bottom - top);
  const xFor = (i: number) => points.length === 1
    ? (left + right) / 2
    : left + (i / (points.length - 1)) * innerW;

  const linePath = points.map((p, i) => `${i === 0 ? 'M' : 'L'} ${xFor(i)} ${yFor(p.resultCount)}`).join(' ');
  const areaPath = `M ${xFor(0)} ${bottom} ${points.map((p, i) => `L ${xFor(i)} ${yFor(p.resultCount)}`).join(' ')} L ${xFor(points.length - 1)} ${bottom} Z`;
  // Distribute 6 ticks across [0, maxValue], rounded so labels read cleanly
  // for any range. Previously the ticks were hardcoded 0..5 which compressed
  // the entire y-axis into a 5-pixel band when maxValue >> 5.
  const ticks = Array.from({ length: 6 }, (_, i) => Math.round((i * maxValue) / 5));
  const thresholdY = yFor(0);

  const formatTime = (iso: string) => {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : `${d.getHours().toString().padStart(2, '0')}:${d.getMinutes().toString().padStart(2, '0')}`;
  };

  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height="200">
      <defs>
        <linearGradient id="task-area" x1="0" x2="0" y1="0" y2="1">
          <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.20" />
          <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
        </linearGradient>
      </defs>
      <line x1={left} x2={right} y1={thresholdY} y2={thresholdY} stroke="var(--warn)" strokeOpacity="0.4" strokeWidth={1} strokeDasharray="3 4" />
      <text x={right - 4} y={thresholdY - 5} fontFamily="var(--font-mono)" fontSize={9} fill="var(--warn)" textAnchor="end">threshold = 0</text>
      {ticks.map((v, i) => (
        <g key={i}>
          <line x1={left} x2={right} y1={yFor(v)} y2={yFor(v)} stroke="var(--border)" strokeOpacity="0.6" />
          <text x={left - 8} y={yFor(v) + 4} fontFamily="var(--font-mono)" fontSize={10} fill="var(--text-subtle)" textAnchor="end">{v}</text>
        </g>
      ))}
      <path d={areaPath} fill="url(#task-area)" />
      <path d={linePath} stroke="var(--brand-500)" strokeWidth={2} fill="none" strokeLinecap="round" />
      {points.map((p, i) => (
        <g key={i}>
          <circle cx={xFor(i)} cy={yFor(p.resultCount)} r={5} fill="var(--surface)" stroke="var(--brand-500)" strokeWidth={2} />
          <text x={xFor(i)} y={yFor(p.resultCount) - 12} fontFamily="var(--font-mono)" fontSize={11} fill="var(--text)" textAnchor="middle" fontWeight={500}>
            {p.resultCount}
          </text>
          <text x={xFor(i)} y={h - 10} fontFamily="var(--font-mono)" fontSize={10} fill="var(--text-subtle)" textAnchor="middle">
            {formatTime(p.sampledAt)}
          </text>
        </g>
      ))}
    </svg>
  );
}
