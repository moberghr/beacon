import { Activity, Clock, Users } from 'lucide-react';
import { Card } from '@/components/beacon';
import { Tabs, type TabDef } from '@/components/Tabs';
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

  const tabs: TabDef<SubscriptionTabKey>[] = [
    {
      key: 'recipients',
      label: <span className="inline-flex items-center gap-1.5"><Users className="size-3.5" /> Recipients</span>,
      count: subscription.recipients.length,
    },
    ...(anomalyOn
      ? [{
          key: 'anomaly' as const,
          label: <span className="inline-flex items-center gap-1.5"><Activity className="size-3.5" /> Anomaly detection</span>,
        }]
      : []),
    {
      key: 'executions',
      label: <span className="inline-flex items-center gap-1.5"><Clock className="size-3.5" /> Execution history</span>,
      count: (executions ?? []).length,
    },
  ];

  return (
    <Card>
      <Tabs tabs={tabs} active={tab} onChange={onTabChange} />

      {tab === 'recipients' && (
        <RecipientsTab subscription={subscription} canWrite={canWrite} isAdmin={isAdmin} />
      )}
      {tab === 'anomaly' && anomalyOn && (
        <AnomalyTab subscription={subscription} />
      )}
      {tab === 'executions' && (
        <ExecutionsTab executions={executions} isLoading={executionsLoading} />
      )}
    </Card>
  );
}
