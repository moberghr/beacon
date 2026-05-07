import type { ControlTowerSubscriptionHealthData } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatNumber, formatPercentage, formatRelativeTime } from '@/lib/format';
import { useControlTowerQuery } from './queries';

// HealthStatus: 0 Green, 1 Amber, 2 Red — see Beacon.Core/Data/Enums/HealthStatus.cs
const HEALTH_PILL: Record<number, { label: string; cls: string }> = {
  0: { label: 'Green', cls: 'pill pill--ok' },
  1: { label: 'Amber', cls: 'pill pill--warn' },
  2: { label: 'Red', cls: 'pill pill--crit' },
};

const COLUMNS: Column<ControlTowerSubscriptionHealthData>[] = [
  {
    key: 'health',
    header: '',
    render: r => {
      const map = HEALTH_PILL[r.healthStatus ?? 0] ?? { label: '?', cls: 'pill' };
      return <span className={map.cls}>{map.label}</span>;
    },
  },
  {
    key: 'name',
    header: 'Subscription',
    render: r => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{r.queryName ?? '—'}</div>
        {r.dataSourceName && (
          <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{r.dataSourceName}</div>
        )}
      </div>
    ),
  },
  {
    key: 'success',
    header: 'Success rate',
    render: r => formatPercentage(r.successRate ?? 0, 1),
  },
  {
    key: 'execs',
    header: 'Executions',
    render: r => formatNumber(r.totalExecutions ?? 0),
  },
  {
    key: 'tasks',
    header: 'Open tasks',
    render: r => formatNumber(r.unresolvedTaskCount ?? 0),
  },
  {
    key: 'lastExec',
    header: 'Last execution',
    render: r =>
      r.lastExecutionTime
        ? <span title={String(r.lastExecutionTime)}>{formatRelativeTime(r.lastExecutionTime)}</span>
        : <span className="muted">never</span>,
  },
];

const GRID_TEMPLATE = '0.6fr 2.4fr 0.9fr 0.9fr 0.9fr 1.1fr';

export default function ControlTowerPage() {
  const { data, isLoading, isError, error, refetch } = useControlTowerQuery();
  const stats = data?.stats;
  const entries = data?.entries ?? [];

  return (
    <div className="page">
      <PageHeader
        title="Control Tower"
        sub={<span className="muted">Real-time health across subscriptions</span>}
        actions={
          <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
            <Icon.Refresh size={14} className="btn__icon" />
            Refresh
          </button>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load Control Tower"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <>
          <div className="kpi-grid">
            <Kpi dot="brand" label="Total" value={formatNumber(stats?.totalSubscriptions ?? 0)} sub="subscriptions" />
            <Kpi dot="ok" label="Healthy" value={formatNumber(stats?.healthySubscriptions ?? 0)} sub="green" />
            <Kpi dot="warn" label="Warning" value={formatNumber(stats?.warningSubscriptions ?? 0)} sub="amber" />
            <Kpi dot="crit" label="Critical" value={formatNumber(stats?.criticalSubscriptions ?? 0)} sub="red" />
          </div>

          <div className="card" style={{ padding: 0, marginTop: 16 }}>
            <DataTable
              columns={COLUMNS}
              rows={entries}
              rowKey={r => r.subscriptionId ?? Math.random()}
              gridTemplate={GRID_TEMPLATE}
              empty={
                <EmptyState
                  icon={<Icon.Tower size={20} />}
                  title={isLoading ? 'Loading subscription health…' : 'No subscriptions yet'}
                  description={isLoading ? '' : 'Create a subscription to start monitoring.'}
                />
              }
            />
          </div>
        </>
      )}
    </div>
  );
}

function Kpi({ dot, label, value, sub }: { dot: string; label: string; value: string; sub: string }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={`kpi__dot kpi__dot--${dot}`}></span>
        <span className="kpi__label">{label}</span>
      </div>
      <div className="kpi__value">{value}</div>
      <div className="kpi__sub"><span className="muted">{sub}</span></div>
    </div>
  );
}
