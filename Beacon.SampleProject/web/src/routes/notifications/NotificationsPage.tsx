import { useNavigate } from 'react-router-dom';
import type { NotificationEntry } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useNotificationsQuery } from './queries';

const STATUS_LABELS: Record<number, { label: string; cls: string }> = {
  1: { label: 'Created', cls: 'pill' },
  2: { label: 'Sent', cls: 'pill pill--ok' },
  3: { label: 'Silenced', cls: 'pill' },
  4: { label: 'No results', cls: 'pill pill--warn' },
  5: { label: 'Timeout', cls: 'pill pill--crit' },
  6: { label: 'Below threshold', cls: 'pill' },
  7: { label: 'Failed', cls: 'pill pill--crit' },
};

const COLUMNS: Column<NotificationEntry>[] = [
  {
    key: 'when',
    header: 'When',
    render: n => <span className="muted mono">{formatDateTime(n.createdTime)}</span>,
  },
  {
    key: 'query',
    header: 'Query',
    render: n => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{n.queryName ?? '—'}</div>
        {n.comment && (
          <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{n.comment}</div>
        )}
      </div>
    ),
  },
  {
    key: 'recipients',
    header: 'Recipients',
    render: n => {
      if (n.recipientNames.length === 0) {
        return <span className="muted">—</span>;
      }
      const head = n.recipientNames.slice(0, 3).join(', ');
      const more = n.recipientNames.length - 3;
      return (
        <span>{head}{more > 0 && <span className="muted"> +{more}</span>}</span>
      );
    },
  },
  {
    key: 'rows',
    header: 'Rows',
    render: n => formatNumber(n.resultCount),
  },
  {
    key: 'status',
    header: 'Status',
    render: n => {
      const map = STATUS_LABELS[n.status] ?? { label: String(n.status), cls: 'pill' };
      return <span className={map.cls}>{map.label}</span>;
    },
  },
];

const GRID_TEMPLATE = '1.4fr 2.2fr 1.6fr 0.6fr 0.9fr';

export default function NotificationsPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useNotificationsQuery();
  const entries = data?.entries ?? [];

  return (
    <div className="page">
      <PageHeader
        title="Notifications"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} of {formatNumber(data?.totalCount ?? 0)}</span>
        }
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
          title="Failed to load notifications"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={COLUMNS}
            rows={entries}
            rowKey={n => n.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={n => navigate(`/notifications/${n.id}`)}
            empty={
              <EmptyState
                icon={<Icon.Bell size={20} />}
                title={isLoading ? 'Loading notifications…' : 'No notifications yet'}
                description={isLoading ? '' : 'Notifications will appear once a subscription fires.'}
              />
            }
          />
        </div>
      )}
    </div>
  );
}
