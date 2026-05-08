import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useAuth, useIsAdmin } from '@/auth/useAuth';
import {
  useArchiveSubscription,
  useSubscriptionDetailQuery,
  useSubscriptionExecutionsQuery,
  useTestSubscription,
} from './queries';
import { SubscriptionHero } from './parts/SubscriptionHero';
import { SubscriptionKpiGrid } from './parts/SubscriptionKpiGrid';
import { SubscriptionInfoCard } from './parts/SubscriptionInfoCard';
import {
  SubscriptionTabsCard,
  type SubscriptionTabKey,
} from './parts/SubscriptionTabsCard';
import { RightRail } from './parts/RightRail';
import { SubscriptionSaveBar } from './parts/SubscriptionSaveBar';

/**
 * Read-mostly subscription detail. Multi-step Add Recipient and Pause/Resume
 * remain Blazor-only until 5c. Test now and Archive are wired through MediatR.
 */
export default function SubscriptionDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const validId = Number.isFinite(id) ? id : undefined;

  const auth = useAuth();
  const isAdmin = useIsAdmin() === true;
  const canWrite = auth.data?.isAuthenticated === true;

  const detail = useSubscriptionDetailQuery(validId);
  const executions = useSubscriptionExecutionsQuery(validId, 200);
  const test = useTestSubscription(validId);
  const archive = useArchiveSubscription(validId);

  const [tab, setTab] = useState<SubscriptionTabKey>('recipients');
  const [confirmingArchive, setConfirmingArchive] = useState(false);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid subscription id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="page">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load subscription"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !detail.data) {
    return (
      <div className="page">
        <div className="muted">Loading subscription…</div>
      </div>
    );
  }

  const subscription = detail.data.detail;
  const execList = executions.data?.entries;
  const totalExecutions = executions.data?.totalCount ?? execList?.length ?? 0;

  const onTest = () => test.mutate();
  const onArchive = () => setConfirmingArchive(true);

  return (
    <div className="page" data-screen-label="04 Subscription Detail">
      <SubscriptionHero
        subscription={subscription}
        canTest={canWrite}
        canArchive={isAdmin}
        isTesting={test.isPending}
        isArchiving={archive.isPending}
        onTest={onTest}
        onArchive={onArchive}
      />

      <SubscriptionKpiGrid
        subscription={subscription}
        executions={execList}
        totalExecutions={totalExecutions}
      />

      <div className="q-layout">
        <div className="q-section">
          <SubscriptionInfoCard subscription={subscription} />
          <SubscriptionTabsCard
            subscription={subscription}
            executions={execList}
            executionsLoading={executions.isLoading}
            tab={tab}
            onTabChange={setTab}
            canWrite={canWrite}
            isAdmin={isAdmin}
          />
        </div>
        <RightRail subscription={subscription} executions={execList} />
      </div>

      <SubscriptionSaveBar
        subscription={subscription}
        totalExecutions={totalExecutions}
        canTest={canWrite}
        canArchive={isAdmin}
        isTesting={test.isPending}
        isArchiving={archive.isPending}
        onTest={onTest}
        onArchive={onArchive}
      />

      <ConfirmDialog
        open={confirmingArchive}
        title="Archive subscription?"
        message="The schedule will stop firing and the row will be moved to archived state. You can recreate it later."
        confirmLabel="Archive"
        destructive
        busy={archive.isPending}
        onConfirm={() => {
          archive.mutate(undefined, {
            onSuccess: () => setConfirmingArchive(false),
            onError: () => setConfirmingArchive(false),
          });
        }}
        onCancel={() => setConfirmingArchive(false)}
      />
    </div>
  );
}
