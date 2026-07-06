import { KPI, KPIGrid, Pill } from '@/components/beacon';
import type { QueryDetail } from '../queries';

interface QueryKpiGridProps {
  query: QueryDetail;
}

export function QueryKpiGrid({ query }: QueryKpiGridProps) {
  const totalExecutions = query.totalExecutions;
  const totalNotifsAcrossDays = query.notificationHistory.reduce(
    (sum, day) => sum + day.totalExecutions,
    0,
  );
  const successfulNotifsAcrossDays = query.notificationHistory.reduce(
    (sum, day) => sum + day.successfulNotifications,
    0,
  );
  const resultsRate = totalNotifsAcrossDays > 0
    ? (successfulNotifsAcrossDays / totalNotifsAcrossDays) * 100
    : null;

  const subsCount = query.subscriptions.length;
  const stepsCount = query.steps.length;

  return (
    <KPIGrid>
      <KPI
        dot="info"
        label="Executions"
        value={totalExecutions.toLocaleString()}
        sub={totalExecutions === 0
          ? <Pill>never run</Pill>
          : <span className="text-text-muted">total runs</span>}
      />
      <KPI
        dot="brand"
        label="Results rate"
        value={resultsRate == null ? 'N/A' : `${resultsRate.toFixed(1)}%`}
        sub={resultsRate == null
          ? <span className="text-text-muted">run to populate</span>
          : <span className="text-text-muted">notifications · last window</span>}
      />
      <KPI
        dot="ok"
        label="Subscriptions"
        value={subsCount.toLocaleString()}
        sub={subsCount === 0
          ? <span className="text-text-muted">not yet subscribed</span>
          : <span className="text-text-muted">active subscribers</span>}
      />
      <KPI
        dot="warn"
        label="Query steps"
        value={stepsCount.toLocaleString()}
        sub={<span className="text-text-muted">{query.isMultiStep ? 'multi-step' : 'single-step'}</span>}
      />
    </KPIGrid>
  );
}
