export interface SparklineProps {
  points: number[];
  color?: string;
  width?: number;
  height?: number;
}

/**
 * Tiny inline-SVG sparkline with a soft gradient fill. Renders an empty
 * SVG when given no data; otherwise normalizes the series to the
 * configured `width`×`height` box. Pure presentational — no theme
 * dependencies beyond the CSS variable token passed in `color`.
 */
export function Sparkline({
  points,
  color = 'var(--brand-500)',
  width = 88,
  height = 28,
}: SparklineProps) {
  if (!points || points.length === 0) {
    return <svg width={width} height={height} />;
  }
  const max = Math.max(...points, 1);
  const min = Math.min(...points, 0);
  const range = max - min || 1;
  const step = points.length > 1 ? width / (points.length - 1) : width;
  const coords = points.map((v, i) => [i * step, height - ((v - min) / range) * (height - 4) - 2]);
  const d = coords.map((c, i) => (i ? 'L' : 'M') + c[0].toFixed(1) + ' ' + c[1].toFixed(1)).join(' ');
  const area = d + ` L ${width} ${height} L 0 ${height} Z`;
  const id = 'sg-' + color.replace(/[^a-z0-9]/gi, '').slice(0, 8) + '-' + points.length;
  return (
    <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" width={width} height={height}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.18" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={area} fill={`url(#${id})`} />
      <path d={d} fill="none" stroke={color} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
