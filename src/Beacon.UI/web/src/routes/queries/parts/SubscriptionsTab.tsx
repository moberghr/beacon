import { Link } from 'react-router-dom';
import { Inbox } from 'lucide-react';
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
      <Link to={`/subscriptions/${s.subscriptionId}`} className="text-brand-600 font-medium">
        {s.name}
      </Link>
    ),
  },
  {
    key: 'subscribers',
    header: 'Subscribers',
    render: s => <span className="text-text-muted">{s.subscribers || '—'}</span>,
  },
  {
    key: 'cron',
    header: 'Cron',
    render: s => <span className="mono">{s.cronExpression || '—'}</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: s => <span className="text-text-muted mono">{formatDateTime(s.createdTime)}</span>,
  },
];

const GRID_TEMPLATE = '0.6fr 2fr 1.4fr 1.4fr 1.4fr';

export function SubscriptionsTab({ subscriptions }: SubscriptionsTabProps) {
  return (
    <DataTable
      columns={COLUMNS}
      rows={subscriptions}
      rowKey={s => s.subscriptionId}
      gridTemplate={GRID_TEMPLATE}
      className="rounded-none border-0 shadow-none"
      empty={
        <EmptyState
          icon={<Inbox size={20} />}
          title="No subscriptions yet"
          description="Add a subscription to schedule this query and notify recipients."
        />
      }
    />
  );
}
