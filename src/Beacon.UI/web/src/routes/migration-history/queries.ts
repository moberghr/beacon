import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { MigrationMode, type MigrationStatus } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

// Local strict mirror of the generated `MigrationExecutionDto` — dates are
// strings on the wire (see `unwrap` docs in @/lib/api).
export interface MigrationExecutionEntry {
  id: number;
  migrationJobId: number;
  migrationJobName: string;
  startedAt: string;
  completedAt: string | null;
  status: MigrationStatus;
  sourceRowsRead: number;
  destinationRowsWritten: number;
  rowsSkipped: number;
  rowsFailed: number;
  executionDuration: string;
  rowsPerSecond: number;
  errorMessage: string | null;
  retryAttempt: number;
  isRetry: boolean;
}

export interface GetMigrationExecutionsResult {
  executions: MigrationExecutionEntry[];
  totalCount: number;
  hasMore: boolean;
}

const MIGRATION_EXECUTIONS_KEY = ['migration-executions'] as const;

export function useMigrationExecutionsQuery() {
  return useQuery({
    queryKey: MIGRATION_EXECUTIONS_KEY,
    queryFn: async () =>
      unwrap<GetMigrationExecutionsResult>(
        await beaconApi().getMigrationExecutions(undefined, undefined, undefined, undefined, 0, 100),
      ),
  });
}

export const MIGRATION_MODE_LABEL: Record<MigrationMode, string> = {
  [MigrationMode.Insert]: 'Insert only',
  [MigrationMode.Upsert]: 'Insert or update',
  [MigrationMode.Truncate]: 'Truncate & insert',
};

export interface CreateMigrationJobPayload {
  name: string;
  description: string;
  dataSourceId: number;
  queryText: string;
  destinationDataSourceId: number;
  destinationTable: string;
  mode: MigrationMode;
  isEnabled: boolean;
  schedule: string | null;
  maxRetries: number;
  timeoutMinutes: number;
  validateBeforeExecution: boolean;
  transformationScript: string | null;
}

export interface CreateMigrationJobResponse {
  migrationJobId: number;
  success: boolean;
  errorMessage: string | null;
}

export function useCreateMigrationJob() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateMigrationJobPayload, CreateMigrationJobResponse>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createMigrationJob(values as never);
        return {
          migrationJobId: r.migrationJobId ?? 0,
          success: r.success ?? false,
          errorMessage: r.errorMessage ?? null,
        };
      },
      invalidate: [MIGRATION_EXECUTIONS_KEY, ['migration-jobs']],
      errorFallback: 'Create migration job failed',
    }),
  );
}
