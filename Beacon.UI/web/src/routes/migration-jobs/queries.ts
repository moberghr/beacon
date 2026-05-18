import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

// NOTE: Phase 3 Batch 7a — handlers added in this slot. Once `npm run codegen`
// is run against /openapi/v1.json, swap to beaconApi() calls.

export const MIGRATION_MODE_LABEL: Record<number, string> = {
  1: 'Insert',
  2: 'Append',
  3: 'Replace',
};

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
    queryFn: () => fetchJson<GetMigrationJobsResult>('/beacon/api/migrations/jobs'),
  });
}

export interface RunMigrationJobResult {
  executionId: number;
  status: number;
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
      mutationFn: ({ id }) =>
        fetchJson<RunMigrationJobResult>(
          `/beacon/api/migrations/jobs/${id}/run`,
          { method: 'POST' },
        ),
      invalidate: (vars) => [MIGRATION_JOBS_KEY, ['migration-executions', vars.id]],
      errorFallback: 'Run migration job failed',
    }),
  );
}

export function useDeleteMigrationJob() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; force?: boolean }, { success: boolean; errorMessage: string | null }>({
      qc,
      mutationFn: ({ id, force }) =>
        fetchJson<{ success: boolean; errorMessage: string | null }>(
          `/beacon/api/migrations/jobs/${id}${force ? '?forceDelete=true' : ''}`,
          { method: 'DELETE' },
        ),
      invalidate: [MIGRATION_JOBS_KEY],
      errorFallback: 'Delete migration job failed',
    }),
  );
}
