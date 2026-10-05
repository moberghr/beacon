import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson, unwrap } from '@/lib/api';
import { usePagedList } from '@/lib/usePagedList';
import { beaconApi } from '@/api/client';
import type { MigrationStatus } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

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

export interface RunMigrationJobResult {
  executionId: number;
  status: MigrationStatus;
  sourceRowsRead: number;
  destinationRowsWritten: number;
  rowsFailed: number;
  errorMessage: string | null;
}

export function useRunMigrationJob() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number }, RunMigrationJobResult>({
      qc,
      mutationFn: async ({ id }) =>
        unwrap<RunMigrationJobResult>(await beaconApi().runMigrationJob(id)),
      // Invalidate the prefix so both the job detail (['migration-executions', id])
      // and the Migration History page (['migration-executions']) refresh.
      invalidate: [MIGRATION_JOBS_KEY, ['migration-executions']],
      errorFallback: 'Run migration job failed',
    }),
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
