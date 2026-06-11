import { useQueryClient, type QueryKey } from '@tanstack/react-query';
import { useHubEvent } from './useHubEvent';

type HubEventName = 'ApprovalUpdated' | 'JobStatusChanged' | 'NotificationCreated';

/**
 * Central map of SignalR events to the query keys they should invalidate.
 * Add new events here instead of subscribing in individual pages — each
 * `useHubEvent` call opens its own subscription on the shared connection,
 * so co-locating the mapping prevents drift and orphaned listeners.
 */
const INVALIDATIONS: Record<HubEventName, ReadonlyArray<QueryKey>> = {
  ApprovalUpdated: [['approvals']],
  JobStatusChanged: [['control-tower']],
  NotificationCreated: [['control-tower'], ['notifications']],
};

/**
 * Mount once at the app shell. Subscribes to every hub event in
 * `INVALIDATIONS` and invalidates the listed query keys when it fires.
 * Pages that need to react to a hub event beyond cache invalidation
 * (e.g. flashing a toast, scrolling a list) can still use `useHubEvent`
 * directly — this hook only handles the invalidate-and-refetch case.
 */
export function useHubInvalidations(): void {
  const qc = useQueryClient();

  useHubEvent('ApprovalUpdated', () => {
    for (const key of INVALIDATIONS.ApprovalUpdated) {
      qc.invalidateQueries({ queryKey: key });
    }
  });

  useHubEvent('JobStatusChanged', () => {
    for (const key of INVALIDATIONS.JobStatusChanged) {
      qc.invalidateQueries({ queryKey: key });
    }
  });

  useHubEvent('NotificationCreated', () => {
    for (const key of INVALIDATIONS.NotificationCreated) {
      qc.invalidateQueries({ queryKey: key });
    }
  });
}
