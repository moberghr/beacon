import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Bell, RefreshCw } from 'lucide-react';
import type { NotificationEntry } from '@/api/generated/beacon-api';
import { Button, PageHeader, Pill, type PillProps } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useNotificationsQuery } from './queries';

const STATUS_LABELS: Record<number, { label: string; tone: PillProps['tone'] }> = {
  1: { label: 'Created', tone: 'neutral' },
  2: { label: 'Sent', tone: 'ok' },
  3: { label: 'Silenced', tone: 'neutral' },
  4: { label: 'No results', tone: 'warn' },
  5: { label: 'Timeout', tone: 'crit' },
  6: { label: 'Below threshold', tone: 'neutral' },
  7: { label: 'Failed', tone: 'crit' },
};

const COLUMNS: Column<NotificationEntry>[] = [
  {
    key: 'when',
    header: 'When',
    render: n => <span className="text-text-muted mono">{formatDateTime(n.createdTime)}</span>,
  },
  {
    key: 'query',
    header: 'Query',
    render: n => (
      <div>
        <div className="font-semibold text-text">{n.queryName ?? '—'}</div>
        {n.comment && (
          <div className="text-text-muted text-xs mt-0.5">{n.comment}</div>
        )}
      </div>
    ),
  },
  {
    key: 'recipients',
    header: 'Recipients',
    render: n => {
      if (n.recipientNames.length === 0) {
        return <span className="text-text-muted">—</span>;
      }
      const head = n.recipientNames.slice(0, 3).join(', ');
      const more = n.recipientNames.length - 3;
      return (
        <span>{head}{more > 0 && <span className="text-text-muted"> +{more}</span>}</span>
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
      const map = STATUS_LABELS[n.status] ?? { label: String(n.status), tone: 'neutral' as const };
      return <Pill tone={map.tone}>{map.label}</Pill>;
    },
  },
];

const GRID_TEMPLATE = '1.4fr 2.2fr 1.6fr 0.6fr 0.9fr';

export default function NotificationsPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useNotificationsQuery();
  const entries = data?.entries ?? [];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Activity"
        emphasis="Notifications"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} of {formatNumber(data?.totalCount ?? 0)}</span>
        }
        actions={
          <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
            Refresh
          </Button>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load notifications"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={COLUMNS}
          rows={entries}
          rowKey={n => n.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={n => navigate(`/notifications/${n.id}`)}
          empty={
            <EmptyState
              icon={<Bell />}
              title={isLoading ? 'Loading notifications…' : 'No notifications yet'}
              description={isLoading ? '' : 'Notifications will appear once a subscription fires.'}
            />
          }
        />
      )}
    </div>
  );
}
