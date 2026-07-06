import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
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

interface GetMigrationJobsResult {
  jobs: MigrationJobListItem[];
}

const MIGRATION_JOBS_KEY = ['migration-jobs'] as const;

export function useMigrationJobsQuery() {
  return useQuery({
    queryKey: MIGRATION_JOBS_KEY,
    queryFn: async () =>
      unwrap<GetMigrationJobsResult>(await beaconApi().getMigrationJobs()),
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
