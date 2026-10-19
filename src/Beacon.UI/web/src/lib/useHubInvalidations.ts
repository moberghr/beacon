import { useQueryClient, type QueryClient, type QueryKey } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { ApprovalUpdatedEvent } from './hub';
import { useHubEvent, useHubReconnected } from './useHubEvent';

type HubEventName = 'ApprovalUpdated' | 'JobStatusChanged';

/**
 * Central map of SignalR events to the query keys they should invalidate.
 * Add new events here instead of subscribing in individual pages — each
 * `useHubEvent` call opens its own subscription on the shared connection,
 * so co-locating the mapping prevents drift and orphaned listeners.
 */
const INVALIDATIONS: Record<HubEventName, ReadonlyArray<QueryKey>> = {
  // A decision moves a query's live steps and versions; a newer submission supersedes a pending one.
  ApprovalUpdated: [['approvals'], ['query'], ['queries'], ['migration-jobs'], ['recipients'], ['data-quality'], ['subscriptions']],
  JobStatusChanged: [['control-tower'], ['migration-jobs'], ['migration-executions']],
};

/** Keys no hub event invalidates but a reconnect still refreshes, so data that changed while offline shows up. */
const RECONNECT_ONLY: ReadonlyArray<QueryKey> = [['notifications']];

/** What the requester of a pending change is told when a newer submission of the same query replaced it. */
export const SUPERSEDED_MESSAGE = 'Your pending change was replaced by a newer submission';

/** The connection came back: refetch every mapped key, since events fired while it was down are lost. */
export function handleHubReconnected(qc: QueryClient): void {
  for (const keys of [...Object.values(INVALIDATIONS), RECONNECT_ONLY]) {
    for (const key of keys) {
      qc.invalidateQueries({ queryKey: key });
    }
  }
}

/**
 * An approval request changed: refetch what shows it, and tell the requester when a newer submission superseded their
 * pending change (the server sends "superseded" to that requester only).
 */
export function handleApprovalUpdated(qc: QueryClient, event: ApprovalUpdatedEvent | undefined): void {
  for (const key of INVALIDATIONS.ApprovalUpdated) {
    qc.invalidateQueries({ queryKey: key });
  }

  if (event?.status === 'superseded') {
    toast.info(SUPERSEDED_MESSAGE);
  }
}

/**
 * Mount once at the app shell. Subscribes to every hub event in
 * `INVALIDATIONS` and invalidates the listed query keys when it fires.
 * Pages that need to react to a hub event beyond cache invalidation
 * (e.g. flashing a toast, scrolling a list) can still use `useHubEvent`
 * directly — this hook only handles the invalidate-and-refetch case.
 */
export function useHubInvalidations(): void {
  const qc = useQueryClient();

  useHubEvent('ApprovalUpdated', event => {
    handleApprovalUpdated(qc, event);
  });

  useHubEvent('JobStatusChanged', () => {
    for (const key of INVALIDATIONS.JobStatusChanged) {
      qc.invalidateQueries({ queryKey: key });
    }
  });

  // Events fired during a transient disconnect are lost — reconcile every
  // mapped query key once the connection comes back.
  useHubReconnected(() => {
    handleHubReconnected(qc);
  });
}
