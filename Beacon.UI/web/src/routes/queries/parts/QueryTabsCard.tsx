import { Zap, Clock, Inbox, GitBranch } from 'lucide-react';
import { Card } from '@/components/beacon';
import { Tabs, type TabDef } from '@/components/Tabs';
import type { QueryDetail } from '../queries';
import { SubscriptionsTab } from './SubscriptionsTab';
import { ExecutionsTab } from './ExecutionsTab';
import { VersionsTab } from './VersionsTab';
import { ChangeHistoryTab } from './ChangeHistoryTab';

export type QueryTabKey = 'subscriptions' | 'executions' | 'versions' | 'history';

interface QueryTabsCardProps {
  query: QueryDetail;
  tab: QueryTabKey;
  onTabChange: (k: QueryTabKey) => void;
}

export function QueryTabsCard({ query, tab, onTabChange }: QueryTabsCardProps) {
  const tabs: TabDef<QueryTabKey>[] = [
    {
      key: 'subscriptions',
      label: <span className="inline-flex items-center gap-1.5"><Inbox size={13} /> Subscriptions</span>,
      count: query.subscriptions.length,
    },
    {
      key: 'executions',
      label: <span className="inline-flex items-center gap-1.5"><Zap size={13} /> Executions</span>,
      count: query.totalExecutions,
    },
    {
      key: 'versions',
      label: <span className="inline-flex items-center gap-1.5"><Clock size={13} /> Versions</span>,
    },
    {
      key: 'history',
      label: <span className="inline-flex items-center gap-1.5"><GitBranch size={13} /> Change history</span>,
    },
  ];

  return (
    <Card>
      <Tabs tabs={tabs} active={tab} onChange={onTabChange} />

      {tab === 'subscriptions' && (
        <SubscriptionsTab subscriptions={query.subscriptions} />
      )}

      {tab === 'executions' && (
        <ExecutionsTab
          notificationHistory={query.notificationHistory}
          executionTimeHistory={query.executionTimeHistory}
        />
      )}

      {tab === 'versions' && (
        <VersionsTab queryId={query.id} />
      )}

      {tab === 'history' && (
        <ChangeHistoryTab queryId={query.id} />
      )}
    </Card>
  );
}
