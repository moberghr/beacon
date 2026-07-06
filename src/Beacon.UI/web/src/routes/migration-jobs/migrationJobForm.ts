import { z } from 'zod';
import { MigrationMode } from '@/lib/enums';
import {
  MIGRATION_MODE_LABEL,
  type CreateMigrationJobPayload,
} from '@/routes/migration-history/queries';

// Shared between NewMigrationJobPage (full page) and CreateMigrationJobDialog
// (stepper) — the two UIs render the same form; schema, defaults, mode options
// and payload mapping live here so they cannot drift.

export const MIGRATION_JOB_SCHEMA = z.object({
  name: z.string().trim().min(1, 'Name is required').max(200),
  description: z.string().trim().min(1, 'Description is required').max(1000),
  dataSourceId: z.number({ message: 'Pick a source data source' })
    .int()
    .min(1, 'Pick a source data source'),
  queryText: z.string().trim().min(1, 'Source SQL is required'),
  destinationDataSourceId: z.number({ message: 'Pick a destination data source' })
    .int()
    .min(1, 'Pick a destination data source'),
  destinationTable: z.string().trim().min(1, 'Destination table is required').max(200),
  mode: z.number().int().min(1).max(3),
  isEnabled: z.boolean(),
  schedule: z.string(),
  maxRetries: z.number({ message: 'Enter retries between 0 and 10' }).int().min(0).max(10),
  timeoutMinutes: z.number({ message: 'Enter a timeout between 1 and 1440 minutes' }).int().min(1).max(1440),
  validateBeforeExecution: z.boolean(),
  transformationScript: z.string(),
});

export type MigrationJobFormValues = z.infer<typeof MIGRATION_JOB_SCHEMA>;

export const MIGRATION_JOB_DEFAULTS: MigrationJobFormValues = {
  name: '',
  description: '',
  dataSourceId: 0,
  queryText: '',
  destinationDataSourceId: 0,
  destinationTable: '',
  mode: MigrationMode.Insert,
  isEnabled: true,
  schedule: '',
  maxRetries: 3,
  timeoutMinutes: 30,
  validateBeforeExecution: true,
  transformationScript: '',
};

export const MODE_OPTIONS: ReadonlyArray<{ value: MigrationMode; label: string }> = [
  { value: MigrationMode.Insert, label: MIGRATION_MODE_LABEL[MigrationMode.Insert] },
  { value: MigrationMode.Upsert, label: MIGRATION_MODE_LABEL[MigrationMode.Upsert] },
  { value: MigrationMode.Truncate, label: MIGRATION_MODE_LABEL[MigrationMode.Truncate] },
];

export function toCreateMigrationJobPayload(v: MigrationJobFormValues): CreateMigrationJobPayload {
  return {
    name: v.name.trim(),
    description: v.description.trim(),
    dataSourceId: v.dataSourceId,
    queryText: v.queryText,
    destinationDataSourceId: v.destinationDataSourceId,
    destinationTable: v.destinationTable.trim(),
    mode: v.mode as MigrationMode,
    isEnabled: v.isEnabled,
    schedule: v.schedule.trim() === '' ? null : v.schedule.trim(),
    maxRetries: v.maxRetries,
    timeoutMinutes: v.timeoutMinutes,
    validateBeforeExecution: v.validateBeforeExecution,
    transformationScript: v.transformationScript.trim() === '' ? null : v.transformationScript.trim(),
  };
}
