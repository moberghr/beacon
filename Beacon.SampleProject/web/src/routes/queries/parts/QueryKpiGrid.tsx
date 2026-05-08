import type { ReactNode } from 'react';
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
    <div className="kpi-grid">
      <Kpi
        dot="info"
        label="Executions"
        value={totalExecutions.toLocaleString()}
        sub={totalExecutions === 0
          ? <span className="pill pill--neutral">never run</span>
          : <span className="muted">total runs</span>}
      />
      <Kpi
        dot="brand"
        label="Results rate"
        value={resultsRate == null ? 'N/A' : `${resultsRate.toFixed(1)}%`}
        sub={resultsRate == null
          ? <span className="muted">run to populate</span>
          : <span className="muted">notifications · last window</span>}
      />
      <Kpi
        dot="ok"
        label="Subscriptions"
        value={subsCount.toLocaleString()}
        sub={subsCount === 0
          ? <span className="muted">not yet subscribed</span>
          : <span className="muted">active subscribers</span>}
      />
      <Kpi
        dot="warn"
        label="Query steps"
        value={stepsCount.toLocaleString()}
        sub={<span className="muted">{query.isMultiStep ? 'multi-step' : 'single-step'}</span>}
      />
    </div>
  );
}

function Kpi({
  dot, label, value, sub,
}: { dot: string; label: string; value: ReactNode; sub: ReactNode }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={`kpi__dot kpi__dot--${dot}`} />
        <span className="kpi__label">{label}</span>
      </div>
      <div className="kpi__value">{value}</div>
      <div className="kpi__sub">{sub}</div>
    </div>
  );
}
