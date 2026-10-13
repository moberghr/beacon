import { useMemo } from 'react';
import { Link } from 'react-router-dom';
import { Clock } from 'lucide-react';
import { Pill, type PillProps } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { NotificationStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import { RunComment } from '@/routes/notifications/RunComment';
import {
  NOTIFICATION_STATUS_LABEL,
  useSubscriptionExecutionsList,
  type SubscriptionDetailRecipient,
  type SubscriptionExecutionEntry,
} from '../queries';

interface ExecutionsTabProps {
  subscriptionId: number;
  /** The subscription's recipients: a run's notes name the recipients they are about. */
  recipients?: SubscriptionDetailRecipient[];
}

const COLUMNS: Column<SubscriptionExecutionEntry>[] = [
  {
    key: 'id',
    header: 'Execution',
    sortKey: 'createdTime',
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

const GRID_TEMPLATE = '1.2fr 1fr 0.7fr 0.8fr 2fr';

function pillTone(status: number): PillProps['tone'] {
  switch (status) {
    case NotificationStatus.NotificationSent: return 'ok';
    case NotificationStatus.Timeout: case NotificationStatus.Failed: case NotificationStatus.Skipped: return 'warn';
    case NotificationStatus.NotificationSilenced: return 'neutral';
    default: return 'info';
  }
}

export function ExecutionsTab({ subscriptionId, recipients }: ExecutionsTabProps) {
  const list = useSubscriptionExecutionsList(subscriptionId);
  const columns = useMemo<Column<SubscriptionExecutionEntry>[]>(() => {
    const names = new Map((recipients ?? []).map(x => [x.id, x.name]));

    return [
      ...COLUMNS,
      {
        key: 'notes',
        header: 'Notes',
        // Why a run notified fewer recipients than it has, or nobody: the reasons the server recorded.
        render: e => e.comment
          ? <RunComment comment={e.comment} recipientNames={names} />
          : <span className="text-text-muted">—</span>,
      },
    ];
  }, [recipients]);

  if (list.isLoading) {
    return <div className="p-4 text-text-muted">Loading executions…</div>;
  }
  return (
    <DataTable
      columns={columns}
      rows={list.items}
      rowKey={e => e.id}
      {...list.tableProps}
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
