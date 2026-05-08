import type { ReactNode } from 'react';

export interface TaskKpiGridProps {
  latestResultCount: number;
  executionCount: number;
  notificationCount: number;
  ageLabel: string;
  ageDetail: string;
}

export function TaskKpiGrid({
  latestResultCount,
  executionCount,
  notificationCount,
  ageLabel,
  ageDetail,
}: TaskKpiGridProps) {
  return (
    <div className="kpi-grid">
      <Kpi
        dot="info"
        label="Latest result count"
        value={latestResultCount.toLocaleString()}
        sub={<span className="pill pill--neutral">latest run</span>}
      />
      <Kpi
        dot="ok"
        label="Executions"
        value={executionCount.toLocaleString()}
        sub={<span className="muted">{executionCount === 1 ? '1 succeeded' : `${executionCount} runs`}</span>}
      />
      <Kpi
        dot="warn"
        label="Notifications"
        value={notificationCount.toLocaleString()}
        sub={notificationCount === 0
          ? <span className="pill pill--warn">none sent</span>
          : <span className="muted">delivered</span>}
      />
      <Kpi
        dot="brand"
        label="Task age"
        value={ageLabel}
        sub={<span className="mono subtle">{ageDetail}</span>}
      />
    </div>
  );
}

function Kpi({
  dot,
  label,
  value,
  sub,
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
