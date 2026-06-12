import { KPI, KPIGrid, Pill } from '@/components/beacon';

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
    <KPIGrid>
      <KPI
        dot="info"
        label="Latest result count"
        value={latestResultCount.toLocaleString()}
        sub={<Pill>latest run</Pill>}
      />
      <KPI
        dot="ok"
        label="Executions"
        value={executionCount.toLocaleString()}
        sub={executionCount === 1 ? '1 run' : `${executionCount} runs`}
      />
      <KPI
        dot="warn"
        label="Notifications"
        value={notificationCount.toLocaleString()}
        sub={notificationCount === 0
          ? <Pill tone="warn">none sent</Pill>
          : 'delivered'}
      />
      <KPI
        dot="brand"
        label="Task age"
        value={ageLabel}
        sub={<span className="mono text-text-subtle">{ageDetail}</span>}
      />
    </KPIGrid>
  );
}
