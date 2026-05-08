import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
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
  return (
    <div className="card">
      <div className="tabs">
        <Tab active={tab === 'subscriptions'} onClick={() => onTabChange('subscriptions')}>
          <Icon.Inbox size={13} /> Subscriptions <span className="tab__count">{query.subscriptions.length}</span>
        </Tab>
        <Tab active={tab === 'executions'} onClick={() => onTabChange('executions')}>
          <Icon.Bolt size={13} /> Executions <span className="tab__count">{query.notificationHistory.length}</span>
        </Tab>
        <Tab active={tab === 'versions'} onClick={() => onTabChange('versions')}>
          <Icon.Clock size={13} /> Versions
        </Tab>
        <Tab active={tab === 'history'} onClick={() => onTabChange('history')}>
          <Icon.Branch size={13} /> Change history
        </Tab>
      </div>

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
    </div>
  );
}

function Tab({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" className={`tab${active ? ' active' : ''}`} onClick={onClick}>
      {children}
    </button>
  );
}
