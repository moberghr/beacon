import { KPI, KPIGrid } from '@/components/beacon';
import type {
  SubscriptionDetail,
  SubscriptionExecutionEntry,
} from '../queries';
import { ANOMALY_SENSITIVITY_LABEL } from '../queries';

interface SubscriptionKpiGridProps {
  subscription: SubscriptionDetail;
  executions: SubscriptionExecutionEntry[] | undefined;
  totalExecutions: number;
}

export function SubscriptionKpiGrid({
  subscription,
  executions,
  totalExecutions,
}: SubscriptionKpiGridProps) {
  const totalNotifs = (executions ?? []).reduce(
    (sum, x) => sum + x.recipientNames.length,
    0,
  );
  const recipientCount = subscription.recipients.length;
  const anomalyEnabled = subscription.anomalyConfig?.enabled === true;
  const latestResultCount = (executions ?? [])
    .slice()
    .sort((a, b) => +new Date(b.createdTime) - +new Date(a.createdTime))[0]?.resultCount ?? 0;

  return (
    <KPIGrid>
      <KPI
        dot="info"
        label="Total executions"
        value={totalExecutions.toLocaleString()}
        sub={totalExecutions === 0 ? 'never run' : 'to date'}
      />
      <KPI
        dot="brand"
        label="Notifications sent"
        value={totalNotifs.toLocaleString()}
        sub={totalNotifs === 0 ? 'none yet' : 'delivered'}
      />
      <KPI
        dot="ok"
        label="Recipients"
        value={recipientCount.toLocaleString()}
        sub={recipientCount === 0 ? 'none configured' : 'configured'}
      />
      {anomalyEnabled ? (
        <KPI
          dot="warn"
          label="Anomaly detection"
          value="ON"
          sub={
            <>
              {ANOMALY_SENSITIVITY_LABEL[subscription.anomalyConfig!.sensitivity]?.toLowerCase() ??
                'configured'}
              {' '}sensitivity
            </>
          }
        />
      ) : (
        <KPI
          dot="warn"
          label="Latest results"
          value={latestResultCount.toLocaleString()}
          sub={totalExecutions === 0 ? 'no executions yet' : 'last run'}
        />
      )}
    </KPIGrid>
  );
}
