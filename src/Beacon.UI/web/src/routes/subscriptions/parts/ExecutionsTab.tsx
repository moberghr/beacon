import { Link } from 'react-router-dom';
import { Clock } from 'lucide-react';
import { Pill, type PillProps } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { NotificationStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  NOTIFICATION_STATUS_LABEL,
  type SubscriptionExecutionEntry,
} from '../queries';

interface ExecutionsTabProps {
  executions: SubscriptionExecutionEntry[] | undefined;
  isLoading: boolean;
}

const COLUMNS: Column<SubscriptionExecutionEntry>[] = [
  {
    key: 'id',
    header: 'Execution',
    render: e => (
      <div className="flex flex-col gap-0.5">
        <Link to={`/notifications/${e.id}`} className="text-brand-600 font-medium">
          #{e.id}
        </Link>
        <span className="text-text-muted mono text-xs">
          {formatDateTime(e.createdTime)}
        </span>
      </div>
    ),
  },
  {
    key: 'recipients',
    header: 'Recipients',
    render: e => (
      <span className="text-text-muted">
        {e.recipientNames.length === 0
          ? 'none'
          : `${e.recipientNames.length} recipient${e.recipientNames.length === 1 ? '' : 's'}`}
      </span>
    ),
  },
  {
    key: 'results',
    header: 'Results',
    render: e => <span className="mono">{e.resultCount.toLocaleString()}</span>,
  },
  {
    key: 'status',
    header: 'Status',
    render: e => (
      <Pill tone={pillTone(e.status)}>
        {NOTIFICATION_STATUS_LABEL[e.status] ?? e.status}
      </Pill>
    ),
  },
];

const GRID_TEMPLATE = '1.4fr 1.2fr 0.8fr 1fr';

function pillTone(status: number): PillProps['tone'] {
  switch (status) {
    case NotificationStatus.NotificationSent: return 'ok';
    case NotificationStatus.Timeout: case NotificationStatus.Failed: return 'warn';
    case NotificationStatus.NotificationSilenced: return 'neutral';
    default: return 'info';
  }
}

export function ExecutionsTab({ executions, isLoading }: ExecutionsTabProps) {
  if (isLoading && (executions?.length ?? 0) === 0) {
    return <div className="p-4 text-text-muted">Loading executions…</div>;
  }
  return (
    <DataTable
      columns={COLUMNS}
      rows={executions ?? []}
      rowKey={e => e.id}
      gridTemplate={GRID_TEMPLATE}
      empty={
        <EmptyState
          icon={<Clock />}
          title="No executions yet"
          description="Click Test now in the hero to run this subscription on demand."
        />
      }
    />
  );
}
