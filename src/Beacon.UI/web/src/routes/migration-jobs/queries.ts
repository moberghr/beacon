import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson, unwrap } from '@/lib/api';
import { usePagedList } from '@/lib/usePagedList';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';
import { useBackgroundTaskStatus } from '@/lib/useBackgroundTask';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import type { ChangeApprovalOutcome } from '@/routes/approvals/queries';

export interface MigrationJobListItem {
  id: number;
  name: string;
  description: string;
  dataSourceId: number;
  dataSourceName: string;
  destinationDataSourceId: number;
  destinationDataSourceName: string;
  destinationTable: string;
  mode: number;
  isEnabled: boolean;
  schedule: string | null;
  createdTime: string;
  /** With the approval workflow on: the job's definition waits for a second person's approval, so it does not run. */
  awaitingApproval?: boolean;
  /** The pending change request of the job, if one waits. */
  pendingApprovalRequestId?: number | null;
  /** While the job waits and no request of it is pending: the last decision on it (a rejection: delete and recreate it). */
  lastApprovalDecision?: ChangeApprovalOutcome | null;
}

const MIGRATION_JOBS_KEY = ['migration-jobs'] as const;

/** The migration jobs grid: server-paged, newest first, name search in the URL. */
export function useMigrationJobsList() {
  return usePagedList<MigrationJobListItem, { search: string }>({
    queryKey: MIGRATION_JOBS_KEY,
    path: '/beacon/api/migrations/jobs',
    filters: { search: '' },
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

/** One migration job by id, for its detail page. */
export function useMigrationJobQuery(id: number | null) {
  return useQuery({
    queryKey: [...MIGRATION_JOBS_KEY, 'by-id', id],
    queryFn: () => fetchJson<MigrationJobListItem>(`/beacon/api/migrations/jobs/${id}`),
    enabled: id != null && Number.isFinite(id),
  });
}

/**
 * Starts a manual run of the job as a background task (202 with its ticket). Its progress is followed with
 * useMigrationJobRunStatus; the run in the request (runMigrationJob) stays for API callers.
 */
export function useStartMigrationJobRun() {
  return useMutation({
    mutationFn: async (id: number) => unwrap<BackgroundTaskTicket>(await beaconApi().startMigrationJobRun(id)),
  });
}

export function useMigrationJobRunStatus(id: number, ticket: BackgroundTaskTicket | null) {
  return useBackgroundTaskStatus([...MIGRATION_JOBS_KEY, id, 'run'] as const, ticket, async (taskId) =>
    unwrap<BackgroundTaskStatus>(await beaconApi().getMigrationJobRun(id, taskId)),
  );
}

export function useDeleteMigrationJob() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; force?: boolean }, { success: boolean; errorMessage: string | null }>({
      qc,
      mutationFn: async ({ id, force }) => {
        const r = await beaconApi().deleteMigrationJob(id, force);
        return { success: r.success ?? false, errorMessage: r.errorMessage ?? null };
      },
      invalidate: [MIGRATION_JOBS_KEY],
      errorFallback: 'Delete migration job failed',
    }),
  );
}
