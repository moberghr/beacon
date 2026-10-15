import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { AlertTriangle, CalendarClock, ShieldAlert, UserX } from 'lucide-react';
import { Banner, Button } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { isNotFound, NOT_FOUND_DETAIL } from '@/lib/api';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { useAccess, useAuth } from '@/auth/useAuth';
import { SetOwnerDialog } from '../users/SetOwnerDialog';
import {
  useArchiveSubscription,
  useReactivateSubscription,
  useSetSubscriptionOwner,
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

export default function SubscriptionDetailPage() {
  const params = useParams<{ id: string }>();
  const navigate = useNavigate();
  const id = Number(params.id);
  const validId = Number.isFinite(id) ? id : undefined;

  const auth = useAuth();
  const access = useAccess();
  const isAdmin = access.isAdmin;
  // Giving a subscription a new owner is a UserAdmin's (an Admin holds it).
  const canChangeOwner = access.hasPermission('UserAdmin');
  const canWrite = auth.data?.isAuthenticated === true;

  const detail = useSubscriptionDetailQuery(validId);
  const executions = useSubscriptionExecutionsQuery(validId, 200);
  const test = useTestSubscription(validId);
  const archive = useArchiveSubscription(validId);
  const reactivate = useReactivateSubscription(validId);
  const setOwner = useSetSubscriptionOwner(validId ?? 0);

  const [tab, setTab] = useState<SubscriptionTabKey>('recipients');
  const [confirmingArchive, setConfirmingArchive] = useState(false);
  const [changingOwner, setChangingOwner] = useState(false);

  if (!Number.isFinite(id)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState icon={<AlertTriangle />} title="Invalid subscription id" />
      </div>
    );
  }

  // A subscription the caller may not read answers 404, like a missing one.
  if (detail.isError && isNotFound(detail.error)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState
          icon={<CalendarClock />}
          title="Subscription not found"
          description={NOT_FOUND_DETAIL}
        />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load subscription"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !detail.data) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <div className="text-text-muted">Loading subscription…</div>
      </div>
    );
  }

  const subscription = detail.data.detail;
  const execList = executions.data?.items;
  const totalExecutions = executions.data?.totalCount ?? execList?.length ?? 0;

  const onTest = () => test.mutate();
  const onArchive = () => setConfirmingArchive(true);
  const onReactivate = () => reactivate.mutate();

  return (
    <div className="flex flex-col gap-5 p-7" data-screen-label="04 Subscription Detail">
      <SubscriptionHero
        subscription={subscription}
        canTest={canWrite}
        canArchive={isAdmin}
        isTesting={test.isPending}
        isArchiving={archive.isPending}
        isReactivating={reactivate.isPending}
        onTest={onTest}
        onArchive={onArchive}
        onReactivate={onReactivate}
        onChangeOwner={canChangeOwner ? () => setChangingOwner(true) : undefined}
      />

      {subscription.notApproved && (
        <Banner
          tone="warn"
          icon={<ShieldAlert />}
          title="The last run was skipped: this query's change is not approved"
          sub="Save the query to submit its SQL for approval, or restore a version and approve it."
        />
      )}

      {subscription.ownerNotAuthorized && (
        <Banner
          tone="warn"
          icon={<UserX />}
          title="Owner not authorized: scheduled runs are skipped"
          sub={
            canChangeOwner
              ? 'The owner this subscription runs as may no longer run its query, so each scheduled run is skipped. Assign an owner who has access to resume it.'
              : 'The owner this subscription runs as may no longer run its query, so each scheduled run is skipped until an Admin or a UserAdmin assigns an owner who has access.'
          }
          actions={
            canChangeOwner ? (
              <Button size="sm" onClick={() => setChangingOwner(true)}>
                Change owner
              </Button>
            ) : undefined
          }
        />
      )}

      <SubscriptionKpiGrid
        subscription={subscription}
        executions={execList}
        totalExecutions={totalExecutions}
      />

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
          <SubscriptionInfoCard subscription={subscription} />
          <SubscriptionTabsCard
            subscription={subscription}
            totalExecutions={totalExecutions}
            tab={tab}
            onTabChange={setTab}
            canWrite={canWrite}
            isAdmin={isAdmin}
          />
        </div>
        <RightRail subscription={subscription} executions={execList} onSelectTab={setTab} />
      </div>

      <SubscriptionSaveBar
        subscription={subscription}
        totalExecutions={totalExecutions}
        canTest={canWrite}
        canArchive={isAdmin}
        isTesting={test.isPending}
        isArchiving={archive.isPending}
        isReactivating={reactivate.isPending}
        onTest={onTest}
        onArchive={onArchive}
        onReactivate={onReactivate}
      />

      {canChangeOwner && changingOwner && (
        <SetOwnerDialog
          open
          noun="subscription"
          description="Scheduled runs run as the owner, so the owner must be able to run this subscription's query. Admins can always change the subscription."
          busy={setOwner.isPending}
          onClose={() => setChangingOwner(false)}
          onSubmit={userId => setOwner.mutate(userId, { onSuccess: () => setChangingOwner(false) })}
        />
      )}

      <ConfirmDialog
        open={confirmingArchive}
        title="Archive subscription?"
        message="The schedule will stop firing and the row will be moved to archived state. You can reactivate it later from the Archived list."
        confirmLabel="Archive"
        destructive
        busy={archive.isPending}
        onConfirm={() => {
          archive.mutate(undefined, {
            onSuccess: () => {
              setConfirmingArchive(false);
              // The detail route now 404s — return to the list.
              navigate('/subscriptions');
            },
            onError: () => setConfirmingArchive(false),
          });
        }}
        onCancel={() => setConfirmingArchive(false)}
      />
    </div>
  );
}
