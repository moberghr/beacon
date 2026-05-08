import { useEffect, useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { ApiError } from '@/lib/api';
import { useDataSourcesQuery, type DataSourceEntry } from '@/routes/data-sources/queries';
import {
  MIGRATION_MODE,
  MIGRATION_MODE_LABEL,
  useCreateMigrationJob,
  type MigrationModeId,
  type CreateMigrationJobPayload,
} from './queries';

// ---------------------------------------------------------------------------
// Schema — Phase 3 Batch 5e ships the core single-step flow. The Blazor page
// supports a multi-step query-builder; that is deferred — the backend accepts
// plain SQL via QueryText (legacy path) which we mirror here.
// ---------------------------------------------------------------------------

const SCHEMA = z.object({
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
  maxRetries: z.number().int().min(0).max(10),
  timeoutMinutes: z.number().int().min(1).max(1440),
  validateBeforeExecution: z.boolean(),
  transformationScript: z.string(),
});

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  name: '',
  description: '',
  dataSourceId: 0,
  queryText: '',
  destinationDataSourceId: 0,
  destinationTable: '',
  mode: MIGRATION_MODE.Insert,
  isEnabled: true,
  schedule: '',
  maxRetries: 3,
  timeoutMinutes: 30,
  validateBeforeExecution: true,
  transformationScript: '',
};

const MODE_OPTIONS: ReadonlyArray<{ value: MigrationModeId; label: string }> = [
  { value: MIGRATION_MODE.Insert, label: MIGRATION_MODE_LABEL[MIGRATION_MODE.Insert] },
  { value: MIGRATION_MODE.Upsert, label: MIGRATION_MODE_LABEL[MIGRATION_MODE.Upsert] },
  { value: MIGRATION_MODE.Truncate, label: MIGRATION_MODE_LABEL[MIGRATION_MODE.Truncate] },
];

interface CreateMigrationJobDialogProps {
  open: boolean;
  onClose: () => void;
}

export function CreateMigrationJobDialog({ open, onClose }: CreateMigrationJobDialogProps) {
  const dataSourcesQuery = useDataSourcesQuery();
  const createMutation = useCreateMigrationJob();

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: DEFAULTS,
    mode: 'onTouched',
  });
  const { register, watch, reset, formState: { errors }, getValues } = form;

  useEffect(() => {
    if (!open) return;
    reset(DEFAULTS);
  }, [open, reset]);

  // Migrations only support DB-style data sources upstream — filter to those
  // that report a database engine type.
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
    const v = getValues();
    const payload: CreateMigrationJobPayload = {
      name: v.name.trim(),
      description: v.description.trim(),
      dataSourceId: v.dataSourceId,
      queryText: v.queryText,
      destinationDataSourceId: v.destinationDataSourceId,
      destinationTable: v.destinationTable.trim(),
      mode: v.mode as MigrationModeId,
      isEnabled: v.isEnabled,
      schedule: v.schedule.trim() === '' ? null : v.schedule.trim(),
      maxRetries: v.maxRetries,
      timeoutMinutes: v.timeoutMinutes,
      validateBeforeExecution: v.validateBeforeExecution,
      transformationScript: v.transformationScript.trim() === '' ? null : v.transformationScript,
    };

    try {
      const result = await createMutation.mutateAsync(payload);
      if (!result.success) {
        toast.error(result.errorMessage ?? 'Failed to create migration job.');
        return;
      }
      toast.success('Migration job created.');
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error
          ? err.message
          : 'Unknown error';
      toast.error(message);
    }
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'basics',
      title: 'Basics',
      description: 'Name and describe the job.',
      fields: ['name', 'description'],
      render: () => (
        <>
          <div className="q-field">
            <label className="q-label" htmlFor="mj-name">
              Job name<span className="q-label__req">*</span>
            </label>
            <input
              id="mj-name"
              type="text"
              className={`q-input${errors.name ? ' q-input--error' : ''}`}
              {...register('name')}
            />
            {errors.name && <div className="q-error">{errors.name.message}</div>}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="mj-desc">
              Description<span className="q-label__req">*</span>
            </label>
            <textarea
              id="mj-desc"
              rows={3}
              className={`q-input${errors.description ? ' q-input--error' : ''}`}
              {...register('description')}
            />
            {errors.description && <div className="q-error">{errors.description.message}</div>}
          </div>
        </>
      ),
    },
    {
      id: 'source',
      title: 'Source',
      description: 'Pick a source and write the extraction query.',
      fields: ['dataSourceId', 'queryText'],
      render: () => (
        <>
          <div className="q-field">
            <label className="q-label" htmlFor="mj-src">
              Source data source<span className="q-label__req">*</span>
            </label>
            <select
              id="mj-src"
              className={`q-input${errors.dataSourceId ? ' q-input--error' : ''}`}
              {...register('dataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select source —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>
                  {ds.name} ({ds.databaseEngineType})
                </option>
              ))}
            </select>
            {dataSourcesQuery.isLoading && (
              <div className="q-help">Loading data sources…</div>
            )}
            {!dataSourcesQuery.isLoading && dbSources.length === 0 && (
              <div className="q-help">
                No database data sources available — add one first under Data sources.
              </div>
            )}
            {errors.dataSourceId && <div className="q-error">{errors.dataSourceId.message}</div>}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="mj-sql">
              Source SQL<span className="q-label__req">*</span>
            </label>
            <textarea
              id="mj-sql"
              rows={6}
              className={`q-input mono${errors.queryText ? ' q-input--error' : ''}`}
              placeholder="SELECT * FROM source_table WHERE updated_at >= :since"
              {...register('queryText')}
            />
            <div className="q-help">
              Single-step extraction query. The multi-step query builder is not yet ported — write
              one SQL statement that returns the rows to migrate.
            </div>
            {errors.queryText && <div className="q-error">{errors.queryText.message}</div>}
          </div>
        </>
      ),
    },
    {
      id: 'destination',
      title: 'Destination',
      description: 'Where to write rows.',
      fields: ['destinationDataSourceId', 'destinationTable', 'mode'],
      render: () => (
        <>
          <div className="q-field">
            <label className="q-label" htmlFor="mj-dst">
              Destination data source<span className="q-label__req">*</span>
            </label>
            <select
              id="mj-dst"
              className={`q-input${errors.destinationDataSourceId ? ' q-input--error' : ''}`}
              {...register('destinationDataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select destination —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>
                  {ds.name} ({ds.databaseEngineType})
                </option>
              ))}
            </select>
            {errors.destinationDataSourceId && (
              <div className="q-error">{errors.destinationDataSourceId.message}</div>
            )}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="mj-table">
              Destination table<span className="q-label__req">*</span>
            </label>
            <input
              id="mj-table"
              type="text"
              className={`q-input mono${errors.destinationTable ? ' q-input--error' : ''}`}
              placeholder="public.target_table"
              {...register('destinationTable')}
            />
            {errors.destinationTable && (
              <div className="q-error">{errors.destinationTable.message}</div>
            )}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="mj-mode">
              Migration mode<span className="q-label__req">*</span>
            </label>
            <select
              id="mj-mode"
              className="q-input"
              {...register('mode', { valueAsNumber: true })}
            >
              {MODE_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
        </>
      ),
    },
    {
      id: 'schedule',
      title: 'Schedule & options',
      description: 'Cron, retries, timeout.',
      fields: ['schedule', 'maxRetries', 'timeoutMinutes'],
      render: () => (
        <>
          <div className="q-field">
            <label className="q-label" htmlFor="mj-cron">Cron schedule</label>
            <input
              id="mj-cron"
              type="text"
              className="q-input mono"
              placeholder="0 2 * * *  (leave empty for manual only)"
              {...register('schedule')}
            />
            <div className="q-help">
              Standard 5-field cron. Leave empty to run only on manual execution.
            </div>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginTop: 14 }}>
            <div className="q-field">
              <label className="q-label" htmlFor="mj-retries">Max retries</label>
              <input
                id="mj-retries"
                type="number"
                className="q-input"
                min={0}
                max={10}
                {...register('maxRetries', { valueAsNumber: true })}
              />
            </div>
            <div className="q-field">
              <label className="q-label" htmlFor="mj-timeout">Timeout (minutes)</label>
              <input
                id="mj-timeout"
                type="number"
                className="q-input"
                min={1}
                max={1440}
                {...register('timeoutMinutes', { valueAsNumber: true })}
              />
            </div>
          </div>

          <div className="q-field" style={{ marginTop: 14, display: 'grid', gap: 6 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input type="checkbox" {...register('isEnabled')} />
              <span>Enable migration job</span>
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input type="checkbox" {...register('validateBeforeExecution')} />
              <span>Validate query before execution</span>
            </label>
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="mj-script">
              Transformation script (optional)
            </label>
            <textarea
              id="mj-script"
              rows={3}
              className="q-input mono"
              placeholder="Use result1, result2 syntax for cross-step transforms"
              {...register('transformationScript')}
            />
          </div>
        </>
      ),
    },
    {
      id: 'review',
      title: 'Review',
      description: 'Confirm and create.',
      render: () => {
        const v = getValues();
        return (
          <dl className="stepper__review">
            <dt>Name</dt>
            <dd>{v.name || <span className="muted">(unset)</span>}</dd>
            <dt>Source</dt>
            <dd>{sourceLabel(sourceId)}</dd>
            <dt>Destination</dt>
            <dd>
              {sourceLabel(destId)}
              {v.destinationTable && <> · <span className="mono">{v.destinationTable}</span></>}
            </dd>
            <dt>Mode</dt>
            <dd>{MIGRATION_MODE_LABEL[mode as MigrationModeId] ?? mode}</dd>
            <dt>Schedule</dt>
            <dd>
              {v.schedule.trim() === ''
                ? <span className="muted">manual only</span>
                : <span className="mono">{v.schedule}</span>}
            </dd>
            <dt>Enabled</dt>
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
