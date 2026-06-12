import { useEffect, useState, type ReactNode } from 'react';
import { Controller, useWatch, type Control, type FieldErrors, type UseFormRegister, type UseFormSetValue } from 'react-hook-form';
import { Bot, CheckCircle2, Cloud, Loader2, Server, Sparkles, XCircle, type LucideIcon } from 'lucide-react';
import { Banner, Button, Card, CardBody, Field, Input, Pill, Select, Seg } from '@/components/beacon';
import { cn } from '@/lib/cn';
import { AiProvider, BedrockAuthMode } from '@/lib/enums';
import {
  AI_PROVIDER_LABEL,
  AWS_REGIONS,
  BEDROCK_AUTH_MODE_LABEL,
  modelsForProvider,
  type AdminSettingsView,
  type ModelOption,
} from './queries';
import type { FormValues } from './lib/admin-settings-form';

type RegisterFn = UseFormRegister<FormValues>;
type ErrorsObj = FieldErrors<FormValues>;
type SetValueFn = UseFormSetValue<FormValues>;
type ControlObj = Control<FormValues>;

// ─── Active provider banner ──────────────────────────────────────────

export function ActiveProviderBanner({ settings }: { settings: AdminSettingsView | undefined }) {
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
  id: AiProvider;
  icon: LucideIcon;
  title: string;
  blurb: string;
}> = [
  { id: AiProvider.OpenAI, icon: Sparkles, title: 'OpenAI', blurb: 'GPT-4o, o1 reasoning models, API key auth.' },
  { id: AiProvider.Claude, icon: Bot, title: 'Anthropic', blurb: 'Claude Opus / Sonnet / Haiku via direct API.' },
  { id: AiProvider.AzureOpenAI, icon: Server, title: 'Azure OpenAI', blurb: 'Self-hosted Azure deployments with your endpoint.' },
  { id: AiProvider.Bedrock, icon: Cloud, title: 'AWS Bedrock', blurb: 'Claude, Llama, and more via AWS — IAM-role friendly.' },
];

export function ProviderPicker({
  value,
  onChange,
}: {
  value: AiProvider;
  onChange: (id: AiProvider) => void;
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

export function OpenAiFields({
  register,
  errors,
  modelKnown,
  control,
  setValue,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  modelKnown: boolean;
  control: ControlObj;
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
        control={control}
        errorMsg={errors.llmModel?.message}
      />
      <ModelPickerField
        label="Fast model"
        options={modelsForProvider(AiProvider.OpenAI)}
        registerName="llmFastModel"
        register={register}
        setValue={setValue}
        control={control}
        errorMsg={errors.llmFastModel?.message}
        hint="Used for cheap/fast classification tasks."
      />
    </div>
  );
}

export function AnthropicFields({
  register,
  errors,
  apiKeySet,
  control,
  setValue,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  apiKeySet: boolean;
  control: ControlObj;
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
        control={control}
        errorMsg={errors.llmModel?.message}
      />
      <ModelPickerField
        label="Fast model"
        options={modelsForProvider(AiProvider.Claude)}
        registerName="llmFastModel"
        register={register}
        setValue={setValue}
        control={control}
        errorMsg={errors.llmFastModel?.message}
      />
    </div>
  );
}

export function AzureFields({
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
      <Field
        label="Endpoint"
        className="md:col-span-2"
        hint={errors.llmEndpoint?.message ? <span className="text-crit">{errors.llmEndpoint.message}</span> : 'e.g. https://my-azure.openai.azure.com'}
      >
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
      <Field
        label="Deployment (model)"
        hint={errors.llmModel?.message ? <span className="text-crit">{errors.llmModel.message}</span> : 'Azure deployment name, not the OpenAI model id.'}
      >
        <Input className="mono" type="text" placeholder="my-gpt-4o" {...register('llmModel')} />
      </Field>
      <Field
        label="Fast deployment"
        hint={errors.llmFastModel?.message ? <span className="text-crit">{errors.llmFastModel.message}</span> : undefined}
      >
        <Input className="mono" type="text" placeholder="my-gpt-4o-mini" {...register('llmFastModel')} />
      </Field>
    </div>
  );
}

export function BedrockFields({
  register,
  errors,
  authMode,
  control,
  setValue,
  accessKeySet,
  secretKeySet,
  sessionTokenSet,
}: {
  register: RegisterFn;
  errors: ErrorsObj;
  authMode: BedrockAuthMode;
  control: ControlObj;
  setValue: SetValueFn;
  accessKeySet: boolean;
  secretKeySet: boolean;
  sessionTokenSet: boolean;
}) {
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <RegionPickerField register={register} setValue={setValue} control={control} errorMsg={errors.llmRegion?.message} />
        <ModelPickerField
          label="Model"
          options={modelsForProvider(AiProvider.Bedrock)}
          registerName="llmModel"
          register={register}
          setValue={setValue}
          control={control}
          errorMsg={errors.llmModel?.message}
        />
        <ModelPickerField
          label="Fast model"
          options={modelsForProvider(AiProvider.Bedrock)}
          registerName="llmFastModel"
          register={register}
          setValue={setValue}
          control={control}
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

/**
 * Controlled preset picker bound to the RHF value: what the select displays is
 * always exactly what the form will save. An empty form value renders an
 * explicit disabled placeholder; a non-preset value (stale cross-provider
 * model, custom id) renders as a disabled "(custom)" option instead of
 * silently displaying the first preset.
 */
export function ModelPickerField({
  label,
  options,
  registerName,
  register,
  setValue,
  control,
  errorMsg,
  hint,
  className,
  placeholder = 'Select a model…',
  customPlaceholder = 'Custom model id',
}: {
  label: string;
  options: ModelOption[];
  registerName: 'llmModel' | 'llmFastModel' | 'llmRegion';
  register: RegisterFn;
  setValue: SetValueFn;
  control: ControlObj;
  errorMsg?: string;
  hint?: string;
  className?: string;
  placeholder?: string;
  customPlaceholder?: string;
}) {
  const formValue = (useWatch({ control, name: registerName }) as string | undefined) ?? '';
  const isPreset = options.some(x => x.id === formValue);
  const [mode, setMode] = useState<'preset' | 'custom'>(formValue !== '' && !isPreset ? 'custom' : 'preset');

  // A reset (settings load) or provider switch can put a non-preset value in
  // the form while the preset select is shown — flip to the custom input so
  // the value stays visible and editable.
  useEffect(() => {
    if (formValue !== '' && !options.some(x => x.id === formValue)) {
      setMode('custom');
    }
  }, [formValue, options]);

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
            value={formValue}
            onChange={e => setValue(registerName, e.currentTarget.value as never, { shouldDirty: true, shouldValidate: true })}
          >
            {!isPreset && (
              <option value={formValue} disabled>
                {formValue === '' ? placeholder : `${formValue} (custom)`}
              </option>
            )}
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
          <Input className="mono flex-1" type="text" placeholder={customPlaceholder} {...register(registerName)} />
          <Button type="button" onClick={() => setMode('preset')}>Presets</Button>
        </div>
      )}
    </Field>
  );
}

export function RegionPickerField({
  register,
  setValue,
  control,
  errorMsg,
}: {
  register: RegisterFn;
  setValue: SetValueFn;
  control: ControlObj;
  errorMsg?: string;
}) {
  return (
    <ModelPickerField
      label="Region"
      options={AWS_REGIONS}
      registerName="llmRegion"
      register={register}
      setValue={setValue}
      control={control}
      errorMsg={errorMsg}
      placeholder="Select a region…"
      customPlaceholder="custom-region-1"
    />
  );
}

// ─── Test connection ─────────────────────────────────────────────────

export function TestConnectionRow({
  onTest,
  pending,
  result,
  error,
}: {
  onTest: () => void;
  pending: boolean;
  result:
    | { ok: boolean; latencyMs: number | null; model: string | null; error: string | null; sample: string | null }
    | undefined;
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

export function Section({ title, sub, children }: {
  title: string;
  sub?: string;
  children: ReactNode;
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
