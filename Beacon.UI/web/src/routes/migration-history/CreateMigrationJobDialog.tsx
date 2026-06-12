import { useEffect, useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { Field, Input, Select, Textarea } from '@/components/beacon';
import { useDataSourcesQuery, type DataSourceEntry } from '@/routes/data-sources/queries';
import { MigrationMode } from '@/lib/enums';
import {
  MIGRATION_JOB_DEFAULTS,
  MIGRATION_JOB_SCHEMA,
  MODE_OPTIONS,
  toCreateMigrationJobPayload,
  type MigrationJobFormValues,
} from '@/routes/migration-jobs/migrationJobForm';
import { MIGRATION_MODE_LABEL, useCreateMigrationJob } from './queries';

// ---------------------------------------------------------------------------
// Phase 3 Batch 5e ships the core single-step flow. The Blazor page supports
// a multi-step query-builder; that is deferred — the backend accepts plain
// SQL via QueryText (legacy path) which we mirror here. The schema, defaults
// and payload mapping are shared with NewMigrationJobPage via
// migrationJobForm.ts.
// ---------------------------------------------------------------------------

type FormValues = MigrationJobFormValues;

const REQ = <span className="text-crit">*</span>;

interface CreateMigrationJobDialogProps {
  open: boolean;
  onClose: () => void;
}

export function CreateMigrationJobDialog({ open, onClose }: CreateMigrationJobDialogProps) {
  const dataSourcesQuery = useDataSourcesQuery();
  const createMutation = useCreateMigrationJob();

  const form = useForm<FormValues>({
    resolver: zodResolver(MIGRATION_JOB_SCHEMA),
    defaultValues: MIGRATION_JOB_DEFAULTS,
    mode: 'onTouched',
  });
  const { register, watch, reset, formState: { errors }, getValues } = form;

  useEffect(() => {
    if (!open) return;
    reset(MIGRATION_JOB_DEFAULTS);
  }, [open, reset]);

  const dbSources: DataSourceEntry[] = useMemo(
    () => (dataSourcesQuery.data?.entries ?? []).filter(x => x.databaseEngineType !== null),
    [dataSourcesQuery.data],
  );

  const sourceId = watch('dataSourceId');
  const destId = watch('destinationDataSourceId');
  const mode = watch('mode');

  const sourceLabel = (id: number) =>
    dbSources.find(x => x.id === id)?.name ?? '—';

  const onFinish = async () => {
    try {
      const result = await createMutation.mutateAsync(toCreateMigrationJobPayload(getValues()));
      if (!result.success) {
        toast.error(result.errorMessage ?? 'Failed to create migration job.');
        return;
      }
      toast.success('Migration job created.');
      onClose();
    } catch {
      // useCreateMigrationJob (createSimpleMutation) already toasts the error.
    }
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'basics',
      title: 'Basics',
      description: 'Name and describe the job.',
      fields: ['name', 'description'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label={<>Job name {REQ}</>}>
            <Input
              type="text"
              aria-invalid={!!errors.name}
              {...register('name')}
            />
            {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
          </Field>

          <Field label={<>Description {REQ}</>}>
            <Textarea
              rows={3}
              aria-invalid={!!errors.description}
              {...register('description')}
            />
            {errors.description && <span className="text-xs text-crit">{errors.description.message}</span>}
          </Field>
        </div>
      ),
    },
    {
      id: 'source',
      title: 'Source',
      description: 'Pick a source and write the extraction query.',
      fields: ['dataSourceId', 'queryText'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field
            label={<>Source data source {REQ}</>}
            hint={
              dataSourcesQuery.isLoading
                ? 'Loading data sources…'
                : dbSources.length === 0
                  ? 'No database data sources available — add one first under Data sources.'
                  : undefined
            }
          >
            <Select
              aria-invalid={!!errors.dataSourceId}
              disabled={dataSourcesQuery.isLoading}
              {...register('dataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select source —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>
                  {ds.name} ({ds.databaseEngineType})
                </option>
              ))}
            </Select>
            {errors.dataSourceId && <span className="text-xs text-crit">{errors.dataSourceId.message}</span>}
          </Field>

          <Field
            label={<>Source SQL {REQ}</>}
            hint={
              <>
                SQL query that extracts rows to migrate. Use <span className="mono">:param</span> placeholders for dynamic values.
                For complex transformations across multiple CTEs or joins, keep the logic within a single SQL statement or use the transformation script below.
              </>
            }
          >
            <Textarea
              rows={6}
              className="mono"
              aria-invalid={!!errors.queryText}
              placeholder="SELECT * FROM source_table WHERE updated_at >= :since"
              {...register('queryText')}
            />
            {errors.queryText && <span className="text-xs text-crit">{errors.queryText.message}</span>}
          </Field>
        </div>
      ),
    },
    {
      id: 'destination',
      title: 'Destination',
      description: 'Where to write rows.',
      fields: ['destinationDataSourceId', 'destinationTable', 'mode'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label={<>Destination data source {REQ}</>}>
            <Select
              aria-invalid={!!errors.destinationDataSourceId}
              disabled={dataSourcesQuery.isLoading}
              {...register('destinationDataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select destination —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>
                  {ds.name} ({ds.databaseEngineType})
                </option>
              ))}
            </Select>
            {errors.destinationDataSourceId && (
              <span className="text-xs text-crit">{errors.destinationDataSourceId.message}</span>
            )}
          </Field>

          <Field label={<>Destination table {REQ}</>}>
            <Input
              type="text"
              className="mono"
              aria-invalid={!!errors.destinationTable}
              placeholder="public.target_table"
              {...register('destinationTable')}
            />
            {errors.destinationTable && (
              <span className="text-xs text-crit">{errors.destinationTable.message}</span>
            )}
          </Field>

          <Field label={<>Migration mode {REQ}</>}>
            <Select {...register('mode', { valueAsNumber: true })}>
              {MODE_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </Select>
          </Field>
        </div>
      ),
    },
    {
      id: 'schedule',
      title: 'Schedule & options',
      description: 'Cron, retries, timeout.',
      fields: ['schedule', 'maxRetries', 'timeoutMinutes'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label="Cron schedule" hint="Standard 5-field cron. Leave empty to run only on manual execution.">
            <Input
              type="text"
              className="mono"
              placeholder="0 2 * * *  (leave empty for manual only)"
              {...register('schedule')}
            />
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field label="Max retries">
              <Input
                type="number"
                min={0}
                max={10}
                {...register('maxRetries', { valueAsNumber: true })}
              />
            </Field>
            <Field label="Timeout (minutes)">
              <Input
                type="number"
                min={1}
                max={1440}
                {...register('timeoutMinutes', { valueAsNumber: true })}
              />
            </Field>
          </div>

          <div className="flex flex-col gap-1.5 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('isEnabled')} />
              <span>Enable migration job</span>
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('validateBeforeExecution')} />
              <span>Validate query before execution</span>
            </label>
          </div>

          <Field label="Transformation script (optional)">
            <Textarea
              rows={3}
              className="mono"
              placeholder="Use result1, result2 syntax for cross-step transforms"
              {...register('transformationScript')}
            />
          </Field>
        </div>
      ),
    },
    {
      id: 'review',
      title: 'Review',
      description: 'Confirm and create.',
      render: () => {
        const v = getValues();
        return (
          <dl className="grid grid-cols-[140px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
            <dt className="text-text-muted">Name</dt>
            <dd>{v.name || <span className="text-text-muted">(unset)</span>}</dd>
            <dt className="text-text-muted">Source</dt>
            <dd>{sourceLabel(sourceId)}</dd>
            <dt className="text-text-muted">Destination</dt>
            <dd>
              {sourceLabel(destId)}
              {v.destinationTable && <> · <span className="mono">{v.destinationTable}</span></>}
            </dd>
            <dt className="text-text-muted">Mode</dt>
            <dd>{MIGRATION_MODE_LABEL[mode as MigrationMode] ?? mode}</dd>
            <dt className="text-text-muted">Schedule</dt>
            <dd>
              {v.schedule.trim() === ''
                ? <span className="text-text-muted">manual only</span>
                : <span className="mono">{v.schedule}</span>}
            </dd>
            <dt className="text-text-muted">Enabled</dt>
            <dd>{v.isEnabled ? 'yes' : 'no'}</dd>
          </dl>
        );
      },
    },
  ];

  return (
    <StepperDialog<FormValues>
      open={open}
      onClose={onClose}
      title="New migration job"
      sub="Extract from a source data source and write to a destination table."
      size="md"
      steps={steps}
      form={form}
      onFinish={onFinish}
      busy={createMutation.isPending}
      finishLabel="Create migration job"
    />
  );
}
