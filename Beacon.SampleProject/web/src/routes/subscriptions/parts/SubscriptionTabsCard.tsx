import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
import {
  type SubscriptionDetail,
  type SubscriptionExecutionEntry,
} from '../queries';
import { RecipientsTab } from './RecipientsTab';
import { AnomalyTab } from './AnomalyTab';
import { ExecutionsTab } from './ExecutionsTab';

export type SubscriptionTabKey = 'recipients' | 'anomaly' | 'executions';

interface SubscriptionTabsCardProps {
  subscription: SubscriptionDetail;
  executions: SubscriptionExecutionEntry[] | undefined;
  executionsLoading: boolean;
  tab: SubscriptionTabKey;
  onTabChange: (k: SubscriptionTabKey) => void;
  canWrite: boolean;
  isAdmin: boolean;
}

export function SubscriptionTabsCard({
  subscription,
  executions,
  executionsLoading,
  tab,
  onTabChange,
  canWrite,
  isAdmin,
}: SubscriptionTabsCardProps) {
  const anomalyOn = subscription.anomalyConfig?.enabled === true;

  return (
    <div className="card">
      <div className="tabs">
        <Tab active={tab === 'recipients'} onClick={() => onTabChange('recipients')}>
          <Icon.Users size={13} /> Recipients
          <span className="tab__count">{subscription.recipients.length}</span>
        </Tab>
        {anomalyOn && (
          <Tab active={tab === 'anomaly'} onClick={() => onTabChange('anomaly')}>
            <Icon.Activity size={13} /> Anomaly detection
          </Tab>
        )}
        <Tab active={tab === 'executions'} onClick={() => onTabChange('executions')}>
          <Icon.Clock size={13} /> Execution history
          <span className="tab__count">{(executions ?? []).length}</span>
        </Tab>
      </div>

      {tab === 'recipients' && (
        <RecipientsTab subscription={subscription} canWrite={canWrite} isAdmin={isAdmin} />
      )}
      {tab === 'anomaly' && anomalyOn && (
        <AnomalyTab subscription={subscription} />
      )}
      {tab === 'executions' && (
        <ExecutionsTab executions={executions} isLoading={executionsLoading} />
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
