import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import { BackgroundTaskState } from '@/lib/enums';
import { useHubEvent } from '@/lib/useHubEvent';

/** How often a running background task's state is read while no JobStatusChanged event arrives (the hub may be off). */
export const BACKGROUND_TASK_POLL_MS = 2000;

export function isBackgroundTaskFinished(state: BackgroundTaskState | null | undefined): boolean {
  return (
    state === BackgroundTaskState.Succeeded ||
    state === BackgroundTaskState.Failed ||
    state === BackgroundTaskState.Skipped
  );
}

/**
 * Follows a background task the server started (a 202 with a ticket) until it finishes: its state is read again when the
 * JobStatusChanged event for its job arrives, and every BACKGROUND_TASK_POLL_MS meanwhile. `state` is the latest known
 * one (the ticket's until the first read); `status` is the last read, with its failure code and warning count.
 */
export function useBackgroundTaskStatus(
  queryKey: readonly unknown[],
  ticket: BackgroundTaskTicket | null,
  readStatus: (taskId: number) => Promise<BackgroundTaskStatus>,
) {
  const qc = useQueryClient();
  const key = [...queryKey, 'background-task', ticket?.taskId ?? null] as const;
  const query = useQuery({
    queryKey: key,
    queryFn: () => readStatus((ticket as BackgroundTaskTicket).taskId),
    enabled: ticket != null,
    refetchInterval: (q) => (isBackgroundTaskFinished(q.state.data?.state) ? false : BACKGROUND_TASK_POLL_MS),
    retry: false,
    staleTime: 0,
  });

  useHubEvent('JobStatusChanged', (event) => {
    if (ticket?.jobId != null && event.jobId === ticket.jobId) {
      void qc.invalidateQueries({ queryKey: key });
    }
  });

  return {
    status: query.data ?? null,
    state: query.data?.state ?? ticket?.state ?? null,
    error: query.error,
  };
}
