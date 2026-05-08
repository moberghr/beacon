import type { ReactNode } from 'react';
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
    <div className="kpi-grid">
      <Kpi
        dot="info"
        label="Total executions"
        value={totalExecutions.toLocaleString()}
        sub={
          <span className="muted">
            {totalExecutions === 0 ? 'never run' : 'to date'}
          </span>
        }
      />
      <Kpi
        dot="brand"
        label="Notifications sent"
        value={totalNotifs.toLocaleString()}
        sub={
          <span className="muted">
            {totalNotifs === 0 ? 'none yet' : 'delivered'}
          </span>
        }
      />
      <Kpi
        dot="ok"
        label="Recipients"
        value={recipientCount.toLocaleString()}
        sub={
          <span className="muted">
            {recipientCount === 0 ? 'none configured' : 'configured'}
          </span>
        }
      />
      {anomalyEnabled ? (
        <Kpi
          dot="warn"
          label="Anomaly detection"
          value="ON"
          sub={
            <span className="muted">
              {ANOMALY_SENSITIVITY_LABEL[subscription.anomalyConfig!.sensitivity]?.toLowerCase() ??
                'configured'}
              {' '}sensitivity
            </span>
          }
        />
      ) : (
        <Kpi
          dot="warn"
          label="Latest results"
          value={latestResultCount.toLocaleString()}
          sub={
            <span className="muted">
              {totalExecutions === 0 ? 'no executions yet' : 'last run'}
            </span>
          }
        />
      )}
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
