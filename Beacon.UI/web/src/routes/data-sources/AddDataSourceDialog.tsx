import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { ApiError } from '@/lib/api';
import {
  Button,
  Field as BField,
  Input,
  Pill,
  Select,
  Textarea,
} from '@/components/beacon';
import {
  DATA_SOURCE_TYPE,
  DATABASE_ENGINE,
  DATABASE_ENGINE_LABEL,
  useCreateDataSource,
  useTestDataSourceConnection,
  type CreateDataSourcePayload,
  type DatabaseEngineId,
} from './queries';

// ---------------------------------------------------------------------------
// Schema — discriminated union per data-source kind. The Blazor source ships
// nine kinds; this slot covers the five that have a usable form upstream
// (Database, CloudWatch, Databricks, BigQuery, Api). Snowflake / AzureSynapse
// are reachable via the Database branch by picking the matching engine.
// ---------------------------------------------------------------------------

const NAME_FIELD = z.string().trim().min(1, 'Name is required').max(120);

const databaseSchema = z.object({
  kind: z.literal('Database'),
  name: NAME_FIELD,
  engine: z.number().int().min(1, 'Pick a database engine'),
  connectionString: z.string().trim().min(1, 'Connection string is required'),
  metadataLoadingEnabled: z.boolean(),
  metadataMaxTables: z.number().int().min(0).max(100000),
  metadataMaxColumnsPerTable: z.number().int().min(0).max(100000),
  metadataLoadTableNamesOnly: z.boolean(),
  metadataExcludeSchemas: z.string(),
  metadataIncludeSchemas: z.string(),
});

const cloudWatchSchema = z.object({
  kind: z.literal('CloudWatch'),
  name: NAME_FIELD,
  region: z.string().trim().min(1, 'AWS region is required'),
  accessKeyId: z.string(),
  secretAccessKey: z.string(),
  logGroups: z.string().trim().min(1, 'At least one log group is required'),
  profileName: z.string(),
});

const databricksSchema = z.object({
  kind: z.literal('Databricks'),
  name: NAME_FIELD,
  host: z.string().trim().min(1, 'Host is required'),
  httpPath: z.string().trim().min(1, 'HTTP path is required'),
  token: z.string().trim().min(1, 'Personal access token is required'),
  catalog: z.string(),
  schema: z.string(),
});

const bigQuerySchema = z.object({
  kind: z.literal('BigQuery'),
  name: NAME_FIELD,
  projectId: z.string().trim().min(1, 'Project id is required'),
  datasetId: z.string(),
  location: z.string(),
  serviceAccountJson: z.string().trim().min(1, 'Service account JSON is required'),
});

const apiSchema = z.object({
  kind: z.literal('Api'),
  name: NAME_FIELD,
  baseUrl: z.string().trim().min(1, 'Base URL is required'),
  specUrl: z.string().trim().min(1, 'OpenAPI spec URL is required'),
  authType: z.enum(['none', 'apiKey', 'bearer', 'basic']),
  apiKeyName: z.string(),
  apiKeyValue: z.string(),
  apiKeyLocation: z.enum(['header', 'query']),
  bearerToken: z.string(),
  basicUsername: z.string(),
  basicPassword: z.string(),
  includePatterns: z.string(),
  excludePatterns: z.string(),
});

const SCHEMA = z.discriminatedUnion('kind', [
  databaseSchema,
  cloudWatchSchema,
  databricksSchema,
  bigQuerySchema,
  apiSchema,
]);

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  kind: 'Database',
  name: '',
  engine: DATABASE_ENGINE.PostgreSQL,
  connectionString: '',
  metadataLoadingEnabled: true,
  metadataMaxTables: 0,
  metadataMaxColumnsPerTable: 0,
  metadataLoadTableNamesOnly: false,
  metadataExcludeSchemas: '',
  metadataIncludeSchemas: '',
};

const KIND_OPTIONS: ReadonlyArray<{ value: FormValues['kind']; label: string }> = [
  { value: 'Database', label: 'Database' },
  { value: 'CloudWatch', label: 'AWS CloudWatch' },
  { value: 'Databricks', label: 'Databricks' },
  { value: 'BigQuery', label: 'BigQuery' },
  { value: 'Api', label: 'API (OpenAPI)' },
];

const ENGINE_OPTIONS: ReadonlyArray<{ value: DatabaseEngineId; label: string }> = [
  { value: DATABASE_ENGINE.PostgreSQL, label: DATABASE_ENGINE_LABEL[DATABASE_ENGINE.PostgreSQL] },
  { value: DATABASE_ENGINE.MSSQL, label: DATABASE_ENGINE_LABEL[DATABASE_ENGINE.MSSQL] },
  { value: DATABASE_ENGINE.MySQL, label: DATABASE_ENGINE_LABEL[DATABASE_ENGINE.MySQL] },
  { value: DATABASE_ENGINE.AzureSynapse, label: DATABASE_ENGINE_LABEL[DATABASE_ENGINE.AzureSynapse] },
  { value: DATABASE_ENGINE.Snowflake, label: DATABASE_ENGINE_LABEL[DATABASE_ENGINE.Snowflake] },
];

// ---------------------------------------------------------------------------
// Helpers — translate form values to the connection-string payload the
// backend expects. Mirrors the Razor `BuildDataSourceData` switch.
// ---------------------------------------------------------------------------

const nullIfEmpty = (s: string) => (s.trim() === '' ? null : s);

const splitCsv = (s: string): string[] =>
  s.split(',').map(x => x.trim()).filter(x => x.length > 0);

interface BuiltPayload {
  dataSourceType: number;
  databaseEngineType: DatabaseEngineId | null;
  connectionString: string;
}

function buildPayload(v: FormValues): BuiltPayload {
  switch (v.kind) {
    case 'Database':
      return {
        dataSourceType: DATA_SOURCE_TYPE.Database,
        databaseEngineType: v.engine as DatabaseEngineId,
        connectionString: v.connectionString,
      };
    case 'CloudWatch':
      return {
        dataSourceType: DATA_SOURCE_TYPE.CloudWatch,
        databaseEngineType: null,
        connectionString: JSON.stringify({
          Region: v.region,
          AccessKeyId: nullIfEmpty(v.accessKeyId),
          SecretAccessKey: nullIfEmpty(v.secretAccessKey),
          LogGroups: splitCsv(v.logGroups),
          ProfileName: nullIfEmpty(v.profileName),
          QueryTimeoutSeconds: 300,
        }),
      };
    case 'Databricks':
      return {
        dataSourceType: DATA_SOURCE_TYPE.Databricks,
        databaseEngineType: null,
        connectionString: JSON.stringify({
          Host: v.host,
          HttpPath: v.httpPath,
          Token: v.token,
          Catalog: nullIfEmpty(v.catalog),
          Schema: nullIfEmpty(v.schema),
          QueryTimeoutSeconds: 300,
        }),
      };
    case 'BigQuery':
      return {
        dataSourceType: DATA_SOURCE_TYPE.BigQuery,
        databaseEngineType: null,
        connectionString: JSON.stringify({
          ProjectId: v.projectId,
          DatasetId: nullIfEmpty(v.datasetId),
          Location: nullIfEmpty(v.location),
          ServiceAccountJson: v.serviceAccountJson,
          QueryTimeoutSeconds: 300,
        }),
      };
    case 'Api': {
      const auth =
        v.authType === 'apiKey'
          ? {
            Type: 'ApiKey',
            ApiKeyName: v.apiKeyName,
            ApiKeyValue: v.apiKeyValue,
            ApiKeyLocation: v.apiKeyLocation,
            Token: null,
            Username: null,
            Password: null,
          }
          : v.authType === 'bearer'
            ? {
              Type: 'Bearer',
              ApiKeyName: null,
              ApiKeyValue: null,
              ApiKeyLocation: null,
              Token: v.bearerToken,
              Username: null,
              Password: null,
            }
            : v.authType === 'basic'
              ? {
                Type: 'Basic',
                ApiKeyName: null,
                ApiKeyValue: null,
                ApiKeyLocation: null,
                Token: null,
                Username: v.basicUsername,
                Password: v.basicPassword,
              }
              : null;
      const include = splitCsv(v.includePatterns);
      const exclude = splitCsv(v.excludePatterns);
      return {
        dataSourceType: DATA_SOURCE_TYPE.Api,
        databaseEngineType: null,
        connectionString: JSON.stringify({
          BaseUrl: v.baseUrl,
          OpenApiSpecUrl: v.specUrl,
          Auth: auth,
          EndpointFilter:
            include.length > 0 || exclude.length > 0
              ? { IncludePathPatterns: include, ExcludePathPatterns: exclude }
              : null,
          TimeoutSeconds: 30,
        }),
      };
    }
  }
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

interface AddDataSourceDialogProps {
  open: boolean;
  onClose: () => void;
}

const REQ = <span className="text-crit">*</span>;

export function AddDataSourceDialog({ open, onClose }: AddDataSourceDialogProps) {
  const createMutation = useCreateDataSource();
  const testMutation = useTestDataSourceConnection();

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: DEFAULTS,
    mode: 'onTouched',
  });
  const { register, watch, reset, formState: { errors }, getValues } = form;

  const [testState, setTestState] = useState<
    | { status: 'idle' }
    | { status: 'pending' }
    | { status: 'success'; message: string }
    | { status: 'error'; message: string }
  >({ status: 'idle' });

  useEffect(() => {
    if (!open) return;
    reset(DEFAULTS);
    setTestState({ status: 'idle' });
  }, [open, reset]);

  const kind = watch('kind');
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const authType = ((watch as any)('authType') as 'none' | 'apiKey' | 'bearer' | 'basic' | undefined) ?? 'none';

  // Switch the form's discriminator without losing already-typed name.
  const switchKind = (next: FormValues['kind']) => {
    const currentName = getValues('name') ?? '';
    setTestState({ status: 'idle' });
    switch (next) {
      case 'Database':
        reset({
          kind: 'Database',
          name: currentName,
          engine: DATABASE_ENGINE.PostgreSQL,
          connectionString: '',
          metadataLoadingEnabled: true,
          metadataMaxTables: 0,
          metadataMaxColumnsPerTable: 0,
          metadataLoadTableNamesOnly: false,
          metadataExcludeSchemas: '',
          metadataIncludeSchemas: '',
        });
        break;
      case 'CloudWatch':
        reset({
          kind: 'CloudWatch',
          name: currentName,
          region: '',
          accessKeyId: '',
          secretAccessKey: '',
          logGroups: '',
          profileName: '',
        });
        break;
      case 'Databricks':
        reset({
          kind: 'Databricks',
          name: currentName,
          host: '',
          httpPath: '',
          token: '',
          catalog: '',
          schema: '',
        });
        break;
      case 'BigQuery':
        reset({
          kind: 'BigQuery',
          name: currentName,
          projectId: '',
          datasetId: '',
          location: '',
          serviceAccountJson: '',
        });
        break;
      case 'Api':
        reset({
          kind: 'Api',
          name: currentName,
          baseUrl: '',
          specUrl: '',
          authType: 'none',
          apiKeyName: '',
          apiKeyValue: '',
          apiKeyLocation: 'header',
          bearerToken: '',
          basicUsername: '',
          basicPassword: '',
          includePatterns: '',
          excludePatterns: '',
        });
        break;
    }
  };

  const onTestConnection = async () => {
    const ok = await form.trigger();
    if (!ok) return;
    const v = getValues();
    const built = buildPayload(v);
    setTestState({ status: 'pending' });
    try {
      const result = await testMutation.mutateAsync({
        name: v.name,
        dataSourceType: built.dataSourceType as 1 | 2 | 6 | 7 | 8,
        databaseEngineType: built.databaseEngineType,
        connectionString: built.connectionString,
      });
      if (result.success) {
        setTestState({ status: 'success', message: result.message ?? 'Connected.' });
      } else {
        setTestState({ status: 'error', message: result.message ?? 'Connection failed.' });
      }
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      setTestState({ status: 'error', message });
    }
  };

  const onFinish = async () => {
    const v = getValues();
    const built = buildPayload(v);
    const payload: CreateDataSourcePayload = {
      name: v.name,
      dataSourceType: built.dataSourceType as 1 | 2 | 6 | 7 | 8,
      databaseEngineType: built.databaseEngineType,
      connectionString: built.connectionString,
      metadataLoadingEnabled: v.kind === 'Database' ? v.metadataLoadingEnabled : false,
      metadataMaxTables: v.kind === 'Database' ? v.metadataMaxTables : 0,
      metadataMaxColumnsPerTable: v.kind === 'Database' ? v.metadataMaxColumnsPerTable : 0,
      metadataLoadTableNamesOnly: v.kind === 'Database' ? v.metadataLoadTableNamesOnly : false,
      metadataExcludeSchemas: v.kind === 'Database' ? splitCsv(v.metadataExcludeSchemas) : [],
      metadataIncludeSchemas: v.kind === 'Database' ? splitCsv(v.metadataIncludeSchemas) : [],
    };
    try {
      const result = await createMutation.mutateAsync(payload);
      if (!result.success) {
        toast.error(result.message ?? 'Failed to create data source.');
        return;
      }
      toast.success(result.message ?? 'Data source created.');
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  };

  // RHF's discriminated-union typing narrows `Path<FormValues>` to the common
  // fields only. Cast through string[] — the actual values are valid paths
  // for whichever branch is currently active.
  const fieldsForConnectionStep = (k: FormValues['kind']): string[] => {
    switch (k) {
      case 'Database': return ['name', 'engine', 'connectionString'];
      case 'CloudWatch': return ['name', 'region', 'logGroups'];
      case 'Databricks': return ['name', 'host', 'httpPath', 'token'];
      case 'BigQuery': return ['name', 'projectId', 'serviceAccountJson'];
      case 'Api': return ['name', 'baseUrl', 'specUrl'];
    }
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'kind',
      title: 'Type',
      description: 'Pick a data source kind.',
      fields: ['kind'],
      render: () => (
        <BField
          label={<>Data source type {REQ}</>}
          hint="Choose the system Beacon will connect to. The next step asks for the credentials it needs."
        >
          <Select value={kind} onChange={e => switchKind(e.target.value as FormValues['kind'])}>
            {KIND_OPTIONS.map(o => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </Select>
        </BField>
      ),
    },
    {
      id: 'connection',
      title: 'Connection',
      description: 'Credentials and endpoints.',
      fields: fieldsForConnectionStep(kind) as never,
      render: () => (
        <div className="flex flex-col gap-3.5">
          <BField label={<>Name {REQ}</>}>
            <Input
              type="text"
              aria-invalid={!!errors.name}
              {...register('name')}
            />
            {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
          </BField>

          {kind === 'Database' && (
            <>
              <BField label={<>Database engine {REQ}</>}>
                <Select {...register('engine', { valueAsNumber: true })}>
                  {ENGINE_OPTIONS.map(o => (
                    <option key={o.value} value={o.value}>{o.label}</option>
                  ))}
                </Select>
                {'engine' in errors && errors.engine && (
                  <span className="text-xs text-crit">{errors.engine.message as string}</span>
                )}
              </BField>

              <BField
                label={<>Connection string {REQ}</>}
                hint="Plaintext over HTTPS only — Beacon encrypts the value at rest before persisting."
              >
                <Input
                  type="password"
                  className="mono"
                  aria-invalid={'connectionString' in errors && !!errors.connectionString}
                  {...register('connectionString' as never)}
                />
                {'connectionString' in errors && errors.connectionString && (
                  <span className="text-xs text-crit">{(errors.connectionString as { message?: string }).message}</span>
                )}
              </BField>

              <div className="flex flex-col gap-1.5">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" {...register('metadataLoadingEnabled' as never)} />
                  <span>Enable metadata loading</span>
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" {...register('metadataLoadTableNamesOnly' as never)} />
                  <span>Load table names only</span>
                </label>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <BField label="Max tables" hint="0 = unlimited">
                  <Input
                    type="number"
                    {...register('metadataMaxTables' as never, { valueAsNumber: true })}
                  />
                </BField>
                <BField label="Max columns / table" hint="0 = unlimited">
                  <Input
                    type="number"
                    {...register('metadataMaxColumnsPerTable' as never, { valueAsNumber: true })}
                  />
                </BField>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <BField label="Exclude schemas">
                  <Input
                    type="text"
                    placeholder="information_schema, pg_catalog"
                    {...register('metadataExcludeSchemas' as never)}
                  />
                </BField>
                <BField label="Include schemas">
                  <Input
                    type="text"
                    placeholder="public, app"
                    {...register('metadataIncludeSchemas' as never)}
                  />
                </BField>
              </div>
            </>
          )}

          {kind === 'CloudWatch' && (
            <>
              <DsField label="AWS region" required name="region" placeholder="us-east-1" register={register} errors={errors} />
              <DsField label="Log groups (comma-separated)" required name="logGroups" placeholder="/aws/lambda/fn1, /aws/lambda/fn2" register={register} errors={errors} />
              <DsField label="Access key id (optional)" name="accessKeyId" register={register} errors={errors} />
              <DsField label="Secret access key (optional)" name="secretAccessKey" type="password" register={register} errors={errors} />
              <DsField label="AWS profile (optional)" name="profileName" register={register} errors={errors} />
            </>
          )}

          {kind === 'Databricks' && (
            <>
              <DsField label="Host" required name="host" placeholder="adb-xxxx.azuredatabricks.net" register={register} errors={errors} />
              <DsField label="HTTP path" required name="httpPath" placeholder="/sql/1.0/warehouses/xxxx" register={register} errors={errors} />
              <DsField label="Personal access token" required name="token" type="password" register={register} errors={errors} />
              <DsField label="Catalog (optional)" name="catalog" register={register} errors={errors} />
              <DsField label="Schema (optional)" name="schema" register={register} errors={errors} />
            </>
          )}

          {kind === 'BigQuery' && (
            <>
              <DsField label="Project id" required name="projectId" register={register} errors={errors} />
              <DsField label="Dataset id (optional)" name="datasetId" register={register} errors={errors} />
              <DsField label="Location (optional)" name="location" placeholder="US, EU, us-central1" register={register} errors={errors} />
              <DsField label="Service account JSON" required name="serviceAccountJson" multiline rows={5} register={register} errors={errors} />
            </>
          )}

          {kind === 'Api' && (
            <>
              <DsField label="Base URL" required name="baseUrl" placeholder="https://api.example.com" register={register} errors={errors} />
              <DsField label="OpenAPI spec URL" required name="specUrl" placeholder="https://api.example.com/openapi.json" register={register} errors={errors} />
              <BField label="Authentication">
                <Select {...register('authType' as never)}>
                  <option value="none">None</option>
                  <option value="apiKey">API key</option>
                  <option value="bearer">Bearer token</option>
                  <option value="basic">Basic auth</option>
                </Select>
              </BField>
              {authType === 'apiKey' && (
                <>
                  <DsField label="Header / param name" required name="apiKeyName" placeholder="X-Api-Key" register={register} errors={errors} />
                  <DsField label="API key value" required name="apiKeyValue" type="password" register={register} errors={errors} />
                  <BField label="Key location">
                    <Select {...register('apiKeyLocation' as never)}>
                      <option value="header">Header</option>
                      <option value="query">Query parameter</option>
                    </Select>
                  </BField>
                </>
              )}
              {authType === 'bearer' && (
                <DsField label="Bearer token" required name="bearerToken" type="password" register={register} errors={errors} />
              )}
              {authType === 'basic' && (
                <>
                  <DsField label="Username" required name="basicUsername" register={register} errors={errors} />
                  <DsField label="Password" required name="basicPassword" type="password" register={register} errors={errors} />
                </>
              )}
              <DsField label="Include path patterns" name="includePatterns" placeholder="/api/v2/**" register={register} errors={errors} />
              <DsField label="Exclude path patterns" name="excludePatterns" placeholder="/api/internal/**" register={register} errors={errors} />
            </>
          )}
        </div>
      ),
    },
    {
      id: 'test',
      title: 'Test & save',
      description: 'Verify and confirm.',
      render: () => {
        const v = getValues();
        return (
          <>
            <dl className="grid grid-cols-[140px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
              <dt className="text-text-muted">Type</dt>
              <dd>{KIND_OPTIONS.find(k => k.value === v.kind)?.label}</dd>
              <dt className="text-text-muted">Name</dt>
              <dd>{v.name || <span className="text-text-muted">(unset)</span>}</dd>
              {v.kind === 'Database' && (
                <>
                  <dt className="text-text-muted">Engine</dt>
                  <dd>{ENGINE_OPTIONS.find(e => e.value === v.engine)?.label ?? '—'}</dd>
                </>
              )}
            </dl>

            <div className="flex items-center gap-2.5 mt-3.5">
              <Button
                type="button"
                onClick={onTestConnection}
                disabled={testState.status === 'pending'}
              >
                {testState.status === 'pending' ? 'Testing…' : 'Test connection'}
              </Button>
              {testState.status === 'success' && <Pill tone="ok">Connected</Pill>}
              {testState.status === 'error' && <Pill tone="crit">Failed</Pill>}
            </div>

            {testState.status === 'success' && testState.message && (
              <div className="text-text-muted mt-2 text-sm">{testState.message}</div>
            )}
            {testState.status === 'error' && (
              <div className="text-xs text-crit mt-2">{testState.message}</div>
            )}
          </>
        );
      },
    },
  ];

  return (
    <StepperDialog<FormValues>
      open={open}
      onClose={onClose}
      title="New data source"
      sub="Connect a database, cloud service, or API as a Beacon data source."
      size="md"
      steps={steps}
      form={form}
      onFinish={onFinish}
      busy={createMutation.isPending}
      finishLabel="Create data source"
    />
  );
}

// ---------------------------------------------------------------------------
// Tiny field helper — keeps the per-engine sections terse.
// ---------------------------------------------------------------------------

interface DsFieldProps {
  label: string;
  name: string;
  required?: boolean;
  type?: 'text' | 'password';
  placeholder?: string;
  multiline?: boolean;
  rows?: number;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  register: any;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  errors: any;
}

function DsField({ label, name, required, type = 'text', placeholder, multiline, rows = 3, register, errors }: DsFieldProps) {
  const err = errors?.[name];
  return (
    <BField label={<>{label}{required && REQ}</>}>
      {multiline ? (
        <Textarea
          rows={rows}
          placeholder={placeholder}
          aria-invalid={!!err}
          {...register(name)}
        />
      ) : (
        <Input
          type={type}
          placeholder={placeholder}
          aria-invalid={!!err}
          {...register(name)}
        />
      )}
      {err && <span className="text-xs text-crit">{err.message as string}</span>}
    </BField>
  );
}
