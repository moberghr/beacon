import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Bell, RefreshCw } from 'lucide-react';
import { Button, Card, PageHeader, Pill, Select, type PillProps } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { NotificationStatus } from '@/lib/enums';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useNotificationsList, type NotificationEntry } from './queries';

const STATUS_LABELS: Record<number, { label: string; tone: PillProps['tone'] }> = {
  [NotificationStatus.Created]: { label: 'Created', tone: 'neutral' },
  [NotificationStatus.NotificationSent]: { label: 'Sent', tone: 'ok' },
  [NotificationStatus.NotificationSilenced]: { label: 'Silenced', tone: 'neutral' },
  [NotificationStatus.NoResults]: { label: 'No results', tone: 'warn' },
  [NotificationStatus.Timeout]: { label: 'Timeout', tone: 'crit' },
  [NotificationStatus.BelowThreshold]: { label: 'Below threshold', tone: 'neutral' },
  [NotificationStatus.Failed]: { label: 'Failed', tone: 'crit' },
};

const COLUMNS: Column<NotificationEntry>[] = [
  {
    key: 'when',
    header: 'When',
    sortKey: 'createdTime',
    render: n => <span className="text-text-muted mono">{formatDateTime(n.createdTime)}</span>,
  },
  {
    key: 'query',
    header: 'Query',
    sortKey: 'queryName',
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
    sortKey: 'resultCount',
    align: 'right',
    render: n => formatNumber(n.resultCount),
  },
  {
    key: 'status',
    header: 'Status',
    sortKey: 'notificationStatus',
    render: n => {
      const map = STATUS_LABELS[n.status] ?? { label: String(n.status), tone: 'neutral' as const };
      return <Pill tone={map.tone}>{map.label}</Pill>;
    },
  },
];

const GRID_TEMPLATE = '1.4fr 2.2fr 1.6fr 0.6fr 0.9fr';

export default function NotificationsPage() {
  const navigate = useNavigate();
  const list = useNotificationsList();
  const { data, items, isLoading, isError, error, refetch } = list;

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Activity"
        emphasis="Notifications"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} execution{data?.totalCount === 1 ? '' : 's'}</span>
        }
        actions={
          <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
            Refresh
          </Button>
        }
      />

      <Card className="p-3 flex gap-2 items-center">
        <span className="text-text-muted text-xs mr-1">Status:</span>
        <Select
          aria-label="Filter by status"
          value={list.filters.status}
          onChange={e => list.setFilter('status', e.target.value)}
          className="w-48"
        >
          <option value="">All statuses</option>
          {Object.entries(STATUS_LABELS).map(([value, x]) => (
            <option key={value} value={value}>{x.label}</option>
          ))}
        </Select>
      </Card>

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
          rows={items}
          rowKey={n => n.id}
          {...list.tableProps}
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
