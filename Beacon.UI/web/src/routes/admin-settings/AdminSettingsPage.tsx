import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useForm, Controller, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import {
  AlertTriangle,
  CheckCircle2,
  XCircle,
  Loader2,
  Cloud,
  Sparkles,
  Bot,
  Server,
} from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import {
  Banner,
  Button,
  Card,
  CardBody,
  Field,
  Input,
  PageHeader,
  Pill,
  Select,
  Seg,
} from '@/components/beacon';
import { describeError } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { cn } from '@/lib/cn';
import { useIsAdmin } from '@/auth/useAuth';
import {
  AI_PROVIDER_LABEL,
  AiProvider,
  AWS_REGIONS,
  BedrockAuthMode,
  BEDROCK_AUTH_MODE_LABEL,
  modelsForProvider,
  useAdminSettingsQuery,
  useUpdateAdminSettings,
  useTestLlmConnection,
  type AdminSettingsView,
  type AiProviderId,
  type BedrockAuthModeId,
  type ModelOption,
} from './queries';
import {
  settingsToForm,
  formToUpdatePayload,
  formToTestPayload,
} from './lib/admin-settings-form';

const SCHEMA = z.object({
  baseUrl: z.string().trim().max(500).optional(),
  llmProvider: z.number().int().min(0).max(3),
  llmModel: z.string().trim().max(200).optional(),
  llmFastModel: z.string().trim().max(200).optional(),
  llmRegion: z.string().trim().max(100).optional(),
  llmBedrockAuthMode: z.number().int().min(0).max(2),
  llmApiKey: z.string().max(500).optional(),
  llmEndpoint: z.string().trim().max(500).optional(),
  llmSessionToken: z.string().max(2000).optional(),
  llmAwsAccessKeyId: z.string().max(200).optional(),
  llmAwsSecretAccessKey: z.string().max(500).optional(),
  llmMaxConcurrentRequests: z.number().int().min(1).max(1000),
  llmTokensPerMinute: z.number().int().min(0),
  llmRequestsPerMinute: z.number().int().min(0),
  llmMonthlyBudget: z.number().min(0),
});

type FormValues = z.infer<typeof SCHEMA>;

export default function AdminSettingsPage() {
  const isAdmin = useIsAdmin();
  const navigate = useNavigate();

  useEffect(() => {
    if (isAdmin === false) {
      toast.error('Admin role required.');
      navigate('/home', { replace: true });
    }
  }, [isAdmin, navigate]);

  if (isAdmin === undefined) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="System" prefix="Admin" emphasis="settings" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isAdmin === false) {
    return null;
  }

  return <AdminSettingsForm />;
}

function AdminSettingsForm() {
  const { data, isLoading, isError, error } = useAdminSettingsQuery();
  const updateMutation = useUpdateAdminSettings();
  const testMutation = useTestLlmConnection();

  const settings = data?.settings;
  const history = data?.history ?? [];

  const defaults = useMemo<FormValues>(() => settingsToForm(settings), [settings]);

  const {
    register,
    handleSubmit,
    reset,
    control,
    setValue,
    getValues,
    formState: { errors, isSubmitting, isDirty },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: defaults,
  });

  useEffect(() => {
    reset(defaults);
  }, [defaults, reset]);

  const provider = useWatch({ control, name: 'llmProvider' }) as AiProviderId;
  const bedrockAuthMode = useWatch({ control, name: 'llmBedrockAuthMode' }) as BedrockAuthModeId;

  const onSubmit = handleSubmit(async values => {
    try {
      await updateMutation.mutateAsync(formToUpdatePayload(values));
      toast.success('Admin settings saved');
    } catch (err) {
      toast.error(describeError(err, 'Save failed'));
    }
  });

  async function handleTest() {
    testMutation.reset();
    try {
      await testMutation.mutateAsync(formToTestPayload(getValues()));
    } catch (err) {
      toast.error(describeError(err, 'Test failed'));
    }
  }

  if (isLoading) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="System" prefix="Admin" emphasis="settings" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="System" prefix="Admin" emphasis="settings" />
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load settings"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4 p-7">
      <PageHeader
        eyebrow="System"
        prefix="Admin"
        emphasis="settings"
        sub={<span className="text-text-muted">System-wide configuration. Changes take effect immediately.</span>}
      />

      <ActiveProviderBanner settings={settings} />

      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <Section title="General" sub="Base URL the LLM and integrations should use to reach this Beacon instance.">
          <Field label="Base URL" hint={errors.baseUrl?.message ? <span className="text-crit">{errors.baseUrl.message}</span> : undefined}>
            <Input type="url" placeholder="https://beacon.example.com" {...register('baseUrl')} />
          </Field>
        </Section>

        <Section
          title="AI provider"
          sub="Pick the large-language-model provider. Only the fields the provider needs are shown."
        >
          <Controller
            control={control}
            name="llmProvider"
            render={({ field }) => (
              <ProviderPicker
                value={field.value as AiProviderId}
                onChange={next => {
                  field.onChange(next);
                  applyProviderDefaults(next, getValues, setValue);
                }}
              />
            )}
          />

          <div className="border-t border-border my-3" />

          {provider === AiProvider.OpenAI && (
            <OpenAiFields
              register={register}
              errors={errors}
              modelKnown={settings?.llmApiKeySet ?? false}
              currentModel={settings?.llmModel ?? null}
              currentFastModel={settings?.llmFastModel ?? null}
              setValue={setValue}
            />
          )}
          {provider === AiProvider.Claude && (
            <AnthropicFields
              register={register}
              errors={errors}
              apiKeySet={settings?.llmApiKeySet ?? false}
              currentModel={settings?.llmModel ?? null}
              currentFastModel={settings?.llmFastModel ?? null}
              setValue={setValue}
            />
          )}
          {provider === AiProvider.AzureOpenAI && (
            <AzureFields
              register={register}
              errors={errors}
              apiKeySet={settings?.llmApiKeySet ?? false}
              endpointSet={settings?.llmEndpointSet ?? false}
            />
          )}
          {provider === AiProvider.Bedrock && (
            <BedrockFields
              register={register}
              errors={errors}
              authMode={bedrockAuthMode}
              control={control}
              setValue={setValue}
              currentModel={settings?.llmModel ?? null}
              currentFastModel={settings?.llmFastModel ?? null}
              accessKeySet={settings?.llmAwsAccessKeyIdSet ?? false}
              secretKeySet={settings?.llmAwsSecretAccessKeySet ?? false}
              sessionTokenSet={settings?.llmSessionTokenSet ?? false}
            />
          )}

          <div className="border-t border-border my-3" />

          <TestConnectionRow
            onTest={handleTest}
            pending={testMutation.isPending}
            result={testMutation.data}
            error={testMutation.error}
          />
        </Section>

        <Section title="Rate limits & budget" sub="Throttle LLM calls so a runaway query doesn't drain the budget.">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Field label="Max concurrent requests" hint={errors.llmMaxConcurrentRequests?.message ? <span className="text-crit">{errors.llmMaxConcurrentRequests.message}</span> : undefined}>
              <Input type="number" min={1} {...register('llmMaxConcurrentRequests', { valueAsNumber: true })} />
            </Field>
            <Field label="Tokens per minute" hint={errors.llmTokensPerMinute?.message ? <span className="text-crit">{errors.llmTokensPerMinute.message}</span> : undefined}>
              <Input type="number" min={0} {...register('llmTokensPerMinute', { valueAsNumber: true })} />
            </Field>
            <Field label="Requests per minute" hint={errors.llmRequestsPerMinute?.message ? <span className="text-crit">{errors.llmRequestsPerMinute.message}</span> : undefined}>
              <Input type="number" min={0} {...register('llmRequestsPerMinute', { valueAsNumber: true })} />
            </Field>
            <Field label="Monthly budget (USD)" hint={errors.llmMonthlyBudget?.message ? <span className="text-crit">{errors.llmMonthlyBudget.message}</span> : undefined}>
              <Input type="number" min={0} step="0.01" {...register('llmMonthlyBudget', { valueAsNumber: true })} />
            </Field>
          </div>
        </Section>

        <div className="flex gap-2 justify-end">
          <Button
            type="button"
            onClick={() => reset(defaults)}
            disabled={!isDirty || isSubmitting}
          >
            Reset
          </Button>
          <Button
            type="submit"
            variant="primary"
            disabled={!isDirty || isSubmitting}
          >
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </Button>
        </div>
      </form>

      {history.length > 0 && (
        <Card className="mt-6">
          <CardBody>
            <h3 className="m-0 mb-2 text-sm font-semibold text-text">Recent changes</h3>
            <div className="flex flex-col gap-1.5">
              {history.slice(0, 20).map((h, i) => (
                <div key={i} className="text-xs">
                  <span className="text-text-muted mono">{formatDateTime(h.changedAt)}</span>
                  <span className="mx-1.5">·</span>
                  <span className="mono">{h.settingKey}</span>
                  <span className="text-text-muted mx-1.5">by</span>
                  <span>{h.changedByUserId ?? 'system'}</span>
                </div>
              ))}
            </div>
          </CardBody>
        </Card>
      )}
    </div>
  );
}

function applyProviderDefaults(
  provider: AiProviderId,
  getValues: () => FormValues,
  setValue: (name: keyof FormValues, value: never, opts?: { shouldDirty?: boolean }) => void,
) {
  // Picking a provider should suggest a sensible default model when the field is empty.
  const v = getValues();
  if (!v.llmModel || v.llmModel.trim().length === 0) {
    const models = modelsForProvider(provider);
    if (models.length > 0) {
      setValue('llmModel', models[0].id as never, { shouldDirty: true });
    }
  }
  // Default a region for Bedrock if not set — pick an EU region since the model presets
  // are EU geo-profile-aware by default.
  if (provider === AiProvider.Bedrock && (!v.llmRegion || v.llmRegion.trim().length === 0)) {
    setValue('llmRegion', 'eu-west-1' as never, { shouldDirty: true });
  }
}

// ─── Active provider banner ──────────────────────────────────────────

function ActiveProviderBanner({ settings }: { settings: AdminSettingsView | undefined }) {
  if (!settings || settings.llmProvider === null) {
    return (
      <Banner tone="warn" title="No active LLM provider">
        Pick a provider below and save to enable AI features.
      </Banner>
    );
  }

  const parts: string[] = [AI_PROVIDER_LABEL[settings.llmProvider]];
  if (settings.llmModel) parts.push(settings.llmModel);
  if (settings.llmProvider === AiProvider.Bedrock) {
    if (settings.llmRegion) parts.push(settings.llmRegion);
    parts.push(BEDROCK_AUTH_MODE_LABEL[settings.llmBedrockAuthMode]);
  }

  return (
    <Card className="border-l-4 border-l-ok">
      <CardBody>
        <div className="flex items-center gap-3">
          <CheckCircle2 className="w-5 h-5 text-ok shrink-0" />
          <div className="flex-1">
            <div className="text-xs text-text-muted uppercase tracking-eyebrow font-semibold">Active provider</div>
            <div className="text-sm mt-0.5">
              {parts.map((p, i) => (
                <span key={i}>
                  {i > 0 && <span className="text-text-subtle mx-1.5">·</span>}
                  <span className={i === 0 ? 'font-semibold' : 'mono'}>{p}</span>
                </span>
              ))}
            </div>
          </div>
        </div>
      </CardBody>
    </Card>
  );
}

// ─── Provider picker ─────────────────────────────────────────────────

const PROVIDER_CARDS: Array<{
  id: AiProviderId;
  icon: typeof Sparkles;
  title: string;
  blurb: string;
}> = [
  { id: AiProvider.OpenAI, icon: Sparkles, title: 'OpenAI', blurb: 'GPT-4o, o1 reasoning models, API key auth.' },
  { id: AiProvider.Claude, icon: Bot, title: 'Anthropic', blurb: 'Claude Opus / Sonnet / Haiku via direct API.' },
  { id: AiProvider.AzureOpenAI, icon: Server, title: 'Azure OpenAI', blurb: 'Self-hosted Azure deployments with your endpoint.' },
  { id: AiProvider.Bedrock, icon: Cloud, title: 'AWS Bedrock', blurb: 'Claude, Llama, and more via AWS — IAM-role friendly.' },
];

function ProviderPicker({
  value,
  onChange,
}: {
  value: AiProviderId;
  onChange: (id: AiProviderId) => void;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
      {PROVIDER_CARDS.map(card => {
        const selected = value === card.id;
        const Icon = card.icon;
        return (
          <button
            type="button"
            key={card.id}
            onClick={() => onChange(card.id)}
            className={cn(
              'flex items-start gap-3 p-3 rounded-md border text-left transition',
              'focus:outline-none focus:shadow-ring',
              selected
                ? 'border-brand-500 bg-brand-50/40 shadow-ring'
                : 'border-border-strong bg-surface hover:border-brand-300 hover:bg-surface-2',
            )}
            aria-pressed={selected}
          >
            <div
              className={cn(
                'w-9 h-9 rounded-md flex items-center justify-center shrink-0',
                selected ? 'bg-brand-500 text-white' : 'bg-surface-2 text-text-muted',
              )}
            >
              <Icon className="w-5 h-5" />
            </div>
            <div className="flex-1">
              <div className="flex items-center gap-2">
                <span className="text-sm font-semibold text-text">{card.title}</span>
                {selected && <Pill tone="info">Selected</Pill>}
              </div>
              <div className="text-xs text-text-muted mt-0.5">{card.blurb}</div>
            </div>
          </button>
        );
      })}
    </div>
  );
}

// ─── Per-provider field sets ─────────────────────────────────────────

type RegisterFn = ReturnType<typeof useForm<FormValues>>['register'];
type ErrorsObj = ReturnType<typeof useForm<FormValues>>['formState']['errors'];
type SetValueFn = ReturnType<typeof useForm<FormValues>>['setValue'];
type ControlObj = ReturnType<typeof useForm<FormValues>>['control'];

function OpenAiFields({
  register,
  errors,
  modelKnown,
  currentModel,
  currentFastModel,
  setValue,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  modelKnown: boolean;
  currentModel: string | null;
  currentFastModel: string | null;
  setValue: SetValueFn;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
      <Field label="API key" className="md:col-span-2">
        <Input
          type="password"
          autoComplete="new-password"
          placeholder={modelKnown ? 'Set — leave blank to keep' : 'sk-…'}
          {...register('llmApiKey')}
        />
      </Field>
      <ModelPickerField
        label="Model"
        options={modelsForProvider(AiProvider.OpenAI)}
        registerName="llmModel"
        register={register}
        setValue={setValue}
        currentValue={currentModel}
        errorMsg={errors.llmModel?.message}
      />
      <ModelPickerField
        label="Fast model"
        options={modelsForProvider(AiProvider.OpenAI)}
        registerName="llmFastModel"
        register={register}
        setValue={setValue}
        currentValue={currentFastModel}
        errorMsg={errors.llmFastModel?.message}
        hint="Used for cheap/fast classification tasks."
      />
    </div>
  );
}

function AnthropicFields({
  register,
  errors,
  apiKeySet,
  currentModel,
  currentFastModel,
  setValue,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  apiKeySet: boolean;
  currentModel: string | null;
  currentFastModel: string | null;
  setValue: SetValueFn;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
      <Field label="API key" className="md:col-span-2">
        <Input
          type="password"
          autoComplete="new-password"
          placeholder={apiKeySet ? 'Set — leave blank to keep' : 'sk-ant-…'}
          {...register('llmApiKey')}
        />
      </Field>
      <ModelPickerField
        label="Model"
        options={modelsForProvider(AiProvider.Claude)}
        registerName="llmModel"
        register={register}
        setValue={setValue}
        currentValue={currentModel}
        errorMsg={errors.llmModel?.message}
      />
      <ModelPickerField
        label="Fast model"
        options={modelsForProvider(AiProvider.Claude)}
        registerName="llmFastModel"
        register={register}
        setValue={setValue}
        currentValue={currentFastModel}
        errorMsg={errors.llmFastModel?.message}
      />
    </div>
  );
}

function AzureFields({
  register,
  errors,
  apiKeySet,
  endpointSet,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  apiKeySet: boolean;
  endpointSet: boolean;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
      <Field label="Endpoint" className="md:col-span-2" hint={errors.llmEndpoint?.message ? <span className="text-crit">{errors.llmEndpoint.message}</span> : 'e.g. https://my-azure.openai.azure.com'}>
        <Input
          type="password"
          autoComplete="new-password"
          placeholder={endpointSet ? 'Set — leave blank to keep' : 'https://…openai.azure.com'}
          {...register('llmEndpoint')}
        />
      </Field>
      <Field label="API key" className="md:col-span-2">
        <Input
          type="password"
          autoComplete="new-password"
          placeholder={apiKeySet ? 'Set — leave blank to keep' : 'Azure API key'}
          {...register('llmApiKey')}
        />
      </Field>
      <Field label="Deployment (model)" hint={errors.llmModel?.message ? <span className="text-crit">{errors.llmModel.message}</span> : 'Azure deployment name, not the OpenAI model id.'}>
        <Input className="mono" type="text" placeholder="my-gpt-4o" {...register('llmModel')} />
      </Field>
      <Field label="Fast deployment" hint={errors.llmFastModel?.message ? <span className="text-crit">{errors.llmFastModel.message}</span> : undefined}>
        <Input className="mono" type="text" placeholder="my-gpt-4o-mini" {...register('llmFastModel')} />
      </Field>
    </div>
  );
}

function BedrockFields({
  register,
  errors,
  authMode,
  control,
  setValue,
  currentModel,
  currentFastModel,
  accessKeySet,
  secretKeySet,
  sessionTokenSet,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  authMode: BedrockAuthModeId;
  control: ControlObj;
  setValue: SetValueFn;
  currentModel: string | null;
  currentFastModel: string | null;
  accessKeySet: boolean;
  secretKeySet: boolean;
  sessionTokenSet: boolean;
}) {
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <RegionPickerField register={register} setValue={setValue} errorMsg={errors.llmRegion?.message} />
        <ModelPickerField
          label="Model"
          options={modelsForProvider(AiProvider.Bedrock)}
          registerName="llmModel"
          register={register}
          setValue={setValue}
          currentValue={currentModel}
          errorMsg={errors.llmModel?.message}
        />
        <ModelPickerField
          label="Fast model"
          options={modelsForProvider(AiProvider.Bedrock)}
          registerName="llmFastModel"
          register={register}
          setValue={setValue}
          currentValue={currentFastModel}
          errorMsg={errors.llmFastModel?.message}
          className="md:col-span-2"
        />
      </div>

      <div className="border-t border-border my-1" />

      <Field label="Credentials">
        <Controller
          control={control}
          name="llmBedrockAuthMode"
          render={({ field }) => (
            <Seg
              value={String(field.value)}
              options={[
                { value: String(BedrockAuthMode.IamRole), label: 'IAM role' },
                { value: String(BedrockAuthMode.AccessKey), label: 'Access keys' },
                { value: String(BedrockAuthMode.TemporaryCredentials), label: 'Temporary credentials' },
              ]}
              onChange={next => field.onChange(Number(next))}
            />
          )}
        />
      </Field>

      {authMode === BedrockAuthMode.IamRole && (
        <Banner tone="info" title="Using the host's IAM role">
          Beacon will use the IAM role attached to this EC2 / ECS / Lambda environment via the
          default AWS credential chain. No keys to configure — just make sure the role has
          <span className="mono"> bedrock:InvokeModel</span> permission for your chosen model.
        </Banner>
      )}

      {(authMode === BedrockAuthMode.AccessKey || authMode === BedrockAuthMode.TemporaryCredentials) && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <Field label="Access key ID">
            <Input
              type="password"
              autoComplete="new-password"
              placeholder={accessKeySet ? 'Set — leave blank to keep' : 'AKIA…'}
              {...register('llmAwsAccessKeyId')}
            />
          </Field>
          <Field label="Secret access key">
            <Input
              type="password"
              autoComplete="new-password"
              placeholder={secretKeySet ? 'Set — leave blank to keep' : 'Secret'}
              {...register('llmAwsSecretAccessKey')}
            />
          </Field>
          {authMode === BedrockAuthMode.TemporaryCredentials && (
            <Field label="Session token" className="md:col-span-2">
              <Input
                type="password"
                autoComplete="new-password"
                placeholder={sessionTokenSet ? 'Set — leave blank to keep' : 'STS session token'}
                {...register('llmSessionToken')}
              />
            </Field>
          )}
        </div>
      )}
    </div>
  );
}

// ─── Model + region picker (preset list + Custom input fallback) ─────

function ModelPickerField({
  label,
  options,
  registerName,
  register,
  setValue,
  currentValue,
  errorMsg,
  hint,
  className,
}: {
  label: string;
  options: ModelOption[];
  registerName: 'llmModel' | 'llmFastModel';
  register: RegisterFn;
  setValue: SetValueFn;
  currentValue: string | null;
  errorMsg?: string;
  hint?: string;
  className?: string;
}) {
  const ids = options.map(x => x.id);
  const initialKnown = currentValue !== null && ids.includes(currentValue);
  const [mode, setMode] = useState<'preset' | 'custom'>(currentValue && !initialKnown ? 'custom' : 'preset');

  return (
    <Field
      label={label}
      className={className}
      hint={
        errorMsg ? <span className="text-crit">{errorMsg}</span>
        : hint ? <span>{hint}</span>
        : undefined
      }
    >
      {mode === 'preset' ? (
        <div className="flex gap-2">
          <Select
            className="mono flex-1"
            defaultValue={currentValue ?? options[0]?.id ?? ''}
            onChange={e => setValue(registerName, e.currentTarget.value as never, { shouldDirty: true })}
          >
            {options.map(o => (
              <option key={o.id} value={o.id}>
                {o.label}{o.hint ? ` — ${o.hint}` : ''}
              </option>
            ))}
          </Select>
          <Button type="button" onClick={() => setMode('custom')}>Custom…</Button>
        </div>
      ) : (
        <div className="flex gap-2">
          <Input className="mono flex-1" type="text" placeholder="Custom model id" {...register(registerName)} />
          <Button type="button" onClick={() => setMode('preset')}>Presets</Button>
        </div>
      )}
    </Field>
  );
}

function RegionPickerField({
  register,
  setValue,
  errorMsg,
}: {
  register: RegisterFn;
  setValue: SetValueFn;
  errorMsg?: string;
}) {
  const [mode, setMode] = useState<'preset' | 'custom'>('preset');
  return (
    <Field
      label="Region"
      hint={errorMsg ? <span className="text-crit">{errorMsg}</span> : undefined}
    >
      {mode === 'preset' ? (
        <div className="flex gap-2">
          <Select
            className="mono flex-1"
            onChange={e => setValue('llmRegion', e.currentTarget.value as never, { shouldDirty: true })}
          >
            {AWS_REGIONS.map(o => (
              <option key={o.id} value={o.id}>{o.label}</option>
            ))}
          </Select>
          <Button type="button" onClick={() => setMode('custom')}>Custom</Button>
        </div>
      ) : (
        <div className="flex gap-2">
          <Input className="mono flex-1" type="text" placeholder="custom-region-1" {...register('llmRegion')} />
          <Button type="button" onClick={() => setMode('preset')}>Presets</Button>
        </div>
      )}
    </Field>
  );
}

// ─── Test connection ─────────────────────────────────────────────────

function TestConnectionRow({
  onTest,
  pending,
  result,
  error,
}: {
  onTest: () => void;
  pending: boolean;
  result: { ok: boolean; latencyMs: number | null; model: string | null; error: string | null; sample: string | null } | undefined;
  error: unknown;
}) {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-3">
        <Button type="button" onClick={onTest} disabled={pending}>
          {pending ? (
            <span className="flex items-center gap-2">
              <Loader2 className="w-3.5 h-3.5 animate-spin" /> Testing…
            </span>
          ) : (
            'Test connection'
          )}
        </Button>
        <span className="text-xs text-text-muted">
          Sends a 1-token ping with the values on this form (without saving).
        </span>
      </div>

      {result && (
        result.ok ? (
          <div className="flex items-start gap-2 text-sm">
            <CheckCircle2 className="w-4 h-4 text-ok mt-0.5 shrink-0" />
            <div>
              <span className="font-semibold text-ok">Connected</span>
              <span className="text-text-muted"> · </span>
              <span className="mono">{result.model}</span>
              {result.latencyMs !== null && (
                <>
                  <span className="text-text-muted"> · </span>
                  <span className="mono">{result.latencyMs}ms</span>
                </>
              )}
              {result.sample && (
                <div className="text-xs text-text-muted mt-0.5">
                  Response: <span className="mono">{result.sample.slice(0, 80)}</span>
                </div>
              )}
            </div>
          </div>
        ) : (
          <div className="flex items-start gap-2 text-sm">
            <XCircle className="w-4 h-4 text-crit mt-0.5 shrink-0" />
            <div>
              <span className="font-semibold text-crit">Failed</span>
              {result.error && (
                <span className="text-text-muted"> · {result.error}</span>
              )}
            </div>
          </div>
        )
      )}

      {error !== null && error !== undefined && !result && (
        <div className="flex items-start gap-2 text-sm">
          <XCircle className="w-4 h-4 text-crit mt-0.5 shrink-0" />
          <span className="text-crit">{error instanceof Error ? error.message : 'Test failed'}</span>
        </div>
      )}
    </div>
  );
}

// ─── Section wrapper ─────────────────────────────────────────────────

function Section({ title, sub, children }: {
  title: string;
  sub?: string;
  children: React.ReactNode;
}) {
  return (
    <Card>
      <CardBody>
        <h3 className="m-0 mb-1 text-sm font-semibold text-text">{title}</h3>
        {sub && <div className="text-text-muted text-xs mb-3">{sub}</div>}
        <div className="flex flex-col gap-3">
          {children}
        </div>
      </CardBody>
    </Card>
  );
}
