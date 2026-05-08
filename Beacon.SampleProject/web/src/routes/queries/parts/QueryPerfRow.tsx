import type { ReactNode } from 'react';
import type { QueryDetail } from '../queries';

interface QueryPerfRowProps {
  query: QueryDetail;
}

export function QueryPerfRow({ query }: QueryPerfRowProps) {
  const hasSamples =
    query.executionTimeHistory.length > 0 || query.totalExecutions > 0;
  const fmt = (ms: number) => (hasSamples && ms > 0 ? ms.toFixed(2) : '—');

  return (
    <div className="row row--3-up">
      <Perf
        dot="ok"
        label="Avg execution"
        value={fmt(query.avgExecutionTimeMs)}
        unit="ms"
        sub={hasSamples ? 'across runs' : 'no samples yet'}
      />
      <Perf
        dot="ok"
        label="Fastest"
        value={fmt(query.minExecutionTimeMs)}
        unit="ms"
        sub={hasSamples ? 'best run' : 'no samples yet'}
      />
      <Perf
        dot="warn"
        label="Slowest"
        value={fmt(query.maxExecutionTimeMs)}
        unit="ms"
        sub={hasSamples ? 'worst run' : 'no samples yet'}
      />
    </div>
  );
}

function Perf({
  dot, label, value, unit, sub,
}: { dot: string; label: string; value: ReactNode; unit?: string; sub: string }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={`kpi__dot kpi__dot--${dot}`} />
        <span className="kpi__label">{label}</span>
      </div>
      <div className="kpi__value">
        {value}
        {unit && <span className="kpi__unit"> {unit}</span>}
      </div>
      <div className="kpi__sub muted">{sub}</div>
    </div>
  );
}
