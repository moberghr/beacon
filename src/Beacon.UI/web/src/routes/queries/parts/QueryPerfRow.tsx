import { KPI, KPIGrid } from '@/components/beacon';
import type { QueryDetail } from '../queries';

interface QueryPerfRowProps {
  query: QueryDetail;
}

export function QueryPerfRow({ query }: QueryPerfRowProps) {
  const hasSamples =
    query.executionTimeHistory.length > 0 || query.totalExecutions > 0;
  const fmt = (ms: number) => (hasSamples && ms > 0 ? ms.toFixed(2) : '—');

  const unit = <span className="text-sm text-text-muted ml-1">ms</span>;

  return (
    <KPIGrid>
      <KPI
        dot="ok"
        label="Avg execution"
        value={<>{fmt(query.avgExecutionTimeMs)}{unit}</>}
        sub={hasSamples ? 'across runs' : 'no samples yet'}
      />
      <KPI
        dot="ok"
        label="Fastest"
        value={<>{fmt(query.minExecutionTimeMs)}{unit}</>}
        sub={hasSamples ? 'best run' : 'no samples yet'}
      />
      <KPI
        dot="warn"
        label="Slowest"
        value={<>{fmt(query.maxExecutionTimeMs)}{unit}</>}
        sub={hasSamples ? 'worst run' : 'no samples yet'}
      />
    </KPIGrid>
  );
}
