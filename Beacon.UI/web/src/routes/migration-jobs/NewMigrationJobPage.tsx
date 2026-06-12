import { useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import { ArrowLeftRight, Database, Layers, RefreshCw } from 'lucide-react';
import {
  PageHeader,
  Button,
  Card,
  CardBody,
  Field as BField,
  Input,
  Select,
  Textarea,
} from '@/components/beacon';
import { useDataSourcesQuery, type DataSourceEntry } from '@/routes/data-sources/queries';
import { useCreateMigrationJob } from '@/routes/migration-history/queries';
import {
  MIGRATION_JOB_DEFAULTS,
  MIGRATION_JOB_SCHEMA,
  MODE_OPTIONS,
  toCreateMigrationJobPayload,
  type MigrationJobFormValues,
} from './migrationJobForm';

const REQ = <span className="text-crit">*</span>;

export default function NewMigrationJobPage() {
  const navigate = useNavigate();
  const dataSourcesQuery = useDataSourcesQuery();
  const createMutation = useCreateMigrationJob();

  const dbSources: DataSourceEntry[] = useMemo(
    () => (dataSourcesQuery.data?.entries ?? []).filter(x => x.databaseEngineType !== null),
    [dataSourcesQuery.data],
  );

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<MigrationJobFormValues>({
    resolver: zodResolver(MIGRATION_JOB_SCHEMA),
    defaultValues: MIGRATION_JOB_DEFAULTS,
    mode: 'onTouched',
  });

  const onSubmit = handleSubmit(async v => {
    try {
      const result = await createMutation.mutateAsync(toCreateMigrationJobPayload(v));
      if (!result.success) {
        toast.error(result.errorMessage ?? 'Failed to create migration job.');
        return;
      }
      toast.success('Migration job created.');
      navigate(`/migration-jobs/${result.migrationJobId}`);
    } catch {
      // useCreateMigrationJob (createSimpleMutation) already toasts the error.
    }
  });

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow={<>Migration jobs <span className="eyebrow-sep">/</span> New</>}
        emphasis="New migration job"
        sub={
          <>
            <Link to="/migration-jobs" className="text-text-muted">Migration jobs</Link>
            <span className="mx-1.5">/</span>
            New
          </>
        }
        actions={
          <Link to="/migration-jobs"><Button>Cancel</Button></Link>
        }
      />

      {!dataSourcesQuery.isLoading && dbSources.length < 2 && (
        <Card>
          <CardBody>
            <span className="text-text-muted">
              Migration jobs need at least two database data sources (source and destination).{' '}
              <Link to="/data-sources" className="text-brand-600 hover:underline">Configure data sources</Link>.
            </span>
          </CardBody>
        </Card>
      )}

      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <Section icon={<Layers className="size-3.5" />} title="Basics">
          <BField label={<>Job name {REQ}</>}>
            <Input
              type="text"
              aria-invalid={!!errors.name}
              {...register('name')}
            />
            {errors.name?.message && <span className="text-xs text-crit">{errors.name.message}</span>}
          </BField>
          <BField label={<>Description {REQ}</>}>
            <Textarea
              rows={3}
              aria-invalid={!!errors.description}
              {...register('description')}
            />
            {errors.description?.message && <span className="text-xs text-crit">{errors.description.message}</span>}
          </BField>
        </Section>

        <Section icon={<Database className="size-3.5" />} title="Source">
          <BField label={<>Source data source {REQ}</>}>
            <Select
              aria-invalid={!!errors.dataSourceId}
              {...register('dataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select source —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>{ds.name} ({ds.databaseEngineType})</option>
              ))}
            </Select>
            {errors.dataSourceId?.message && <span className="text-xs text-crit">{errors.dataSourceId.message}</span>}
          </BField>
          <BField label={<>Source SQL {REQ}</>} hint="Use :param placeholders for dynamic values.">
            <Textarea
              rows={6}
              className="mono"
              aria-invalid={!!errors.queryText}
              placeholder="SELECT * FROM source_table WHERE updated_at >= :since"
              {...register('queryText')}
            />
            {errors.queryText?.message && <span className="text-xs text-crit">{errors.queryText.message}</span>}
          </BField>
        </Section>

        <Section icon={<ArrowLeftRight className="size-3.5" />} title="Destination">
          <BField label={<>Destination data source {REQ}</>}>
            <Select
              aria-invalid={!!errors.destinationDataSourceId}
              {...register('destinationDataSourceId', { setValueAs: v => v === '' || v == null ? 0 : Number(v) })}
            >
              <option value={0}>— Select destination —</option>
              {dbSources.map(ds => (
                <option key={ds.id} value={ds.id}>{ds.name} ({ds.databaseEngineType})</option>
              ))}
            </Select>
            {errors.destinationDataSourceId?.message && <span className="text-xs text-crit">{errors.destinationDataSourceId.message}</span>}
          </BField>
          <BField label={<>Destination table {REQ}</>}>
            <Input
              type="text"
              className="mono"
              aria-invalid={!!errors.destinationTable}
              placeholder="public.target_table"
              {...register('destinationTable')}
            />
            {errors.destinationTable?.message && <span className="text-xs text-crit">{errors.destinationTable.message}</span>}
          </BField>
          <BField label={<>Migration mode {REQ}</>}>
            <Select {...register('mode', { valueAsNumber: true })}>
              {MODE_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </Select>
          </BField>
        </Section>

        <Section icon={<RefreshCw className="size-3.5" />} title="Schedule & options">
          <BField label="Cron schedule" hint="Standard 5-field cron. Leave empty for manual-only execution.">
            <Input
              type="text"
              className="mono"
              placeholder="0 2 * * *"
              {...register('schedule')}
            />
          </BField>
          <div className="grid grid-cols-2 gap-3">
            <BField label="Max retries">
              <Input
                type="number"
                min={0}
                max={10}
                {...register('maxRetries', { valueAsNumber: true })}
              />
            </BField>
            <BField label="Timeout (minutes)">
              <Input
                type="number"
                min={1}
                max={1440}
                {...register('timeoutMinutes', { valueAsNumber: true })}
              />
            </BField>
          </div>
          <div className="flex flex-col gap-1.5 mt-1.5 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('isEnabled')} />
              <span>Enable migration job</span>
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('validateBeforeExecution')} />
              <span>Validate query before execution</span>
            </label>
          </div>
          <BField label="Transformation script (optional)">
            <Textarea
              rows={3}
              className="mono"
              placeholder="Use result1, result2 syntax for cross-step transforms"
              {...register('transformationScript')}
            />
          </BField>
        </Section>

        <div className="flex justify-end gap-2.5">
          <Link to="/migration-jobs"><Button type="button">Cancel</Button></Link>
          <Button
            variant="primary"
            type="submit"
            disabled={isSubmitting || createMutation.isPending || dbSources.length < 2}
          >
            {createMutation.isPending ? 'Creating…' : 'Create migration job'}
          </Button>
        </div>
      </form>
    </div>
  );
}

function Section({ icon, title, children }: { icon: React.ReactNode; title: string; children: React.ReactNode }) {
  return (
    <Card>
      <CardBody>
        <h3 className="m-0 mb-3 text-sm font-semibold text-text flex items-center gap-1.5">
          {icon} {title}
        </h3>
        <div className="flex flex-col gap-3">
          {children}
        </div>
      </CardBody>
    </Card>
  );
}
