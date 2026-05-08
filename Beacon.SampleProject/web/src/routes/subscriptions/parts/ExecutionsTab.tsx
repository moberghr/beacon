import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
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
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <Link to={`/notifications/${e.id}`} style={{ color: 'var(--brand-600)', fontWeight: 500 }}>
          #{e.id}
        </Link>
        <span className="muted mono" style={{ fontSize: 11 }}>
          {formatDateTime(e.createdTime)}
        </span>
      </div>
    ),
  },
  {
    key: 'recipients',
    header: 'Recipients',
    render: e => (
      <span className="muted">
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
      <span
        className={`pill ${pillClass(e.status)} mono`}
        style={{ fontSize: 10 }}
      >
        {NOTIFICATION_STATUS_LABEL[e.status] ?? e.status}
      </span>
    ),
  },
];

const GRID_TEMPLATE = '1.4fr 1.2fr 0.8fr 1fr';

function pillClass(status: number): string {
  switch (status) {
    case 2: return 'pill--ok';
    case 5: case 7: return 'pill--warn';
    case 3: return 'pill--neutral';
    default: return 'pill--info';
  }
}

export function ExecutionsTab({ executions, isLoading }: ExecutionsTabProps) {
  if (isLoading && (executions?.length ?? 0) === 0) {
    return <div style={{ padding: 16 }} className="muted">Loading executions…</div>;
  }
  return (
    <div style={{ padding: 0 }}>
      <DataTable
        columns={COLUMNS}
        rows={executions ?? []}
        rowKey={e => e.id}
        gridTemplate={GRID_TEMPLATE}
        empty={
          <EmptyState
            icon={<Icon.Clock size={20} />}
            title="No executions yet"
            description="Click Test now in the hero to run this subscription on demand."
          />
        }
      />
    </div>
  );
}
