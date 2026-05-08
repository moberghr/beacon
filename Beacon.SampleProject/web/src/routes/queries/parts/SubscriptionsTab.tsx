import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime } from '@/lib/format';
import type { QuerySubscriptionListItem } from '../queries';

interface SubscriptionsTabProps {
  subscriptions: QuerySubscriptionListItem[];
}

const COLUMNS: Column<QuerySubscriptionListItem>[] = [
  {
    key: 'id',
    header: 'ID',
    render: s => <span className="mono">#{s.subscriptionId}</span>,
  },
  {
    key: 'name',
    header: 'Name',
    render: s => (
      <Link to={`/subscriptions/${s.subscriptionId}`} style={{ color: 'var(--brand-600)', fontWeight: 500 }}>
        {s.name}
      </Link>
    ),
  },
  {
    key: 'subscribers',
    header: 'Subscribers',
    render: s => <span className="muted">{s.subscribers || '—'}</span>,
  },
  {
    key: 'cron',
    header: 'Cron',
    render: s => <span className="mono">{s.cronExpression || '—'}</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: s => <span className="muted mono">{formatDateTime(s.createdTime)}</span>,
  },
];

const GRID_TEMPLATE = '0.6fr 2fr 1.4fr 1.4fr 1.4fr';

export function SubscriptionsTab({ subscriptions }: SubscriptionsTabProps) {
  return (
    <div style={{ padding: 0 }}>
      <DataTable
        columns={COLUMNS}
        rows={subscriptions}
        rowKey={s => s.subscriptionId}
        gridTemplate={GRID_TEMPLATE}
        empty={
          <EmptyState
            icon={<Icon.Inbox size={20} />}
            title="No subscriptions yet"
            description="Add a subscription to schedule this query and notify recipients."
          />
        }
      />
    </div>
  );
}
