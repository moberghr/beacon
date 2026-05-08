import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { fetchJson } from '@/lib/api';

const MIGRATION_EXECUTIONS_KEY = ['migration-executions'] as const;

export function useMigrationExecutionsQuery() {
  return useQuery({
    queryKey: MIGRATION_EXECUTIONS_KEY,
    queryFn: () =>
      beaconApi().getMigrationExecutions(undefined, undefined, undefined, undefined, 0, 100),
  });
}

// Mirrors Beacon.Core.Data.Enums.MigrationMode.
export const MIGRATION_MODE = {
  Insert: 1,
  Upsert: 2,
  Truncate: 3,
} as const;
export type MigrationModeId = typeof MIGRATION_MODE[keyof typeof MIGRATION_MODE];

export const MIGRATION_MODE_LABEL: Record<MigrationModeId, string> = {
  [MIGRATION_MODE.Insert]: 'Insert only',
  [MIGRATION_MODE.Upsert]: 'Insert or update',
  [MIGRATION_MODE.Truncate]: 'Truncate & insert',
};

export interface CreateMigrationJobPayload {
  name: string;
  description: string;
  dataSourceId: number;
  queryText: string;
  destinationDataSourceId: number;
  destinationTable: string;
  mode: MigrationModeId;
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
  return useMutation({
    mutationFn: (values: CreateMigrationJobPayload) =>
      fetchJson<CreateMigrationJobResponse>('/beacon/api/migrations/jobs', {
        method: 'POST',
        body: JSON.stringify(values),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: MIGRATION_EXECUTIONS_KEY }),
  });
}
