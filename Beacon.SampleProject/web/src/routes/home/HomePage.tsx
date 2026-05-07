import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { formatNumber, formatPercentage } from '@/lib/format';
import { useHomeStatsQuery } from './queries';

export default function HomePage() {
  const { data, isLoading, isError, error, refetch } = useHomeStatsQuery();

  return (
    <div className="page">
      <PageHeader
        title="Home"
        sub={<span className="muted">Beacon overview</span>}
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
          title="Failed to load dashboard"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <>
          <div className="kpi-grid">
            <Kpi
              dot="brand"
              label="Projects"
              value={isLoading ? '—' : formatNumber(data?.projects.entries?.length ?? 0)}
              sub="total"
            />
            <Kpi
              dot="info"
              label="Subscriptions"
              value={isLoading ? '—' : formatNumber(data?.controlTower?.totalSubscriptions ?? 0)}
              sub="monitored"
            />
            <Kpi
              dot="ok"
              label="Healthy"
              value={isLoading ? '—' : formatNumber(data?.controlTower?.healthySubscriptions ?? 0)}
              sub="green status"
            />
            <Kpi
              dot="crit"
              label="Critical"
              value={isLoading ? '—' : formatNumber(data?.controlTower?.criticalSubscriptions ?? 0)}
              sub="red status"
            />
          </div>

          <div className="kpi-grid" style={{ marginTop: 16 }}>
            <Kpi
              dot="warn"
              label="Notifications"
              value={isLoading ? '—' : formatNumber(data?.notifications.totalCount ?? 0)}
              sub="lifetime"
            />
            <Kpi
              dot="info"
              label="Unresolved tasks"
              value={isLoading ? '—' : formatNumber(data?.controlTower?.totalUnresolvedTasks ?? 0)}
              sub="open"
            />
            <Kpi
              dot="warn"
              label="Anomalies (30d)"
              value={isLoading ? '—' : formatNumber(data?.controlTower?.totalAnomalies30Days ?? 0)}
              sub="detected"
            />
            <Kpi
              dot="ok"
              label="Success rate"
              value={isLoading ? '—' : formatPercentage(data?.controlTower?.overallSuccessRate ?? 0, 1)}
              sub="overall"
            />
          </div>

          <div className="card" style={{ marginTop: 24 }}>
            <div className="card__head">
              <Icon.Bolt size={14} className="muted" />
              <h3 className="card__title">Quick navigation</h3>
            </div>
            <div className="card__body">
              <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                <Link className="btn" to="/app/projects">
                  <Icon.Folder size={14} className="btn__icon" />
                  Projects
                </Link>
                <Link className="btn" to="/app/notifications">
                  <Icon.Bell size={14} className="btn__icon" />
                  Notifications
                </Link>
                <Link className="btn" to="/app/control-tower">
                  <Icon.Tower size={14} className="btn__icon" />
                  Control Tower
                </Link>
                <Link className="btn" to="/app/migration-history">
                  <Icon.ArrowsLR size={14} className="btn__icon" />
                  Migration history
                </Link>
              </div>
            </div>
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
