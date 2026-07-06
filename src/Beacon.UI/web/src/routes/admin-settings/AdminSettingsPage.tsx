import { useEffect, useMemo } from 'react';
import { useForm, Controller, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import { AlertTriangle } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, Card, CardBody, Field, Input, PageHeader } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import { useRequireAdmin } from '@/auth/useRequireAdmin';
import { AiProvider, type BedrockAuthMode } from '@/lib/enums';
import {
  modelsForProvider,
  useAdminSettingsQuery,
  useUpdateAdminSettings,
  useTestLlmConnection,
} from './queries';
import {
  ADMIN_SETTINGS_SCHEMA,
  settingsToForm,
  formToUpdatePayload,
  formToTestPayload,
  type FormValues,
} from './lib/admin-settings-form';
import {
  ActiveProviderBanner,
  AnthropicFields,
  AzureFields,
  BedrockFields,
  OpenAiFields,
  ProviderPicker,
  Section,
  TestConnectionRow,
} from './provider-fields';

export default function AdminSettingsPage() {
  const isAdmin = useRequireAdmin();

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
    resolver: zodResolver(ADMIN_SETTINGS_SCHEMA),
    defaultValues: defaults,
  });

  useEffect(() => {
    reset(defaults);
  }, [defaults, reset]);

  const provider = useWatch({ control, name: 'llmProvider' }) as AiProvider;
  const bedrockAuthMode = useWatch({ control, name: 'llmBedrockAuthMode' }) as BedrockAuthMode;

  const onSubmit = handleSubmit(async values => {
    try {
      await updateMutation.mutateAsync(formToUpdatePayload(values));
      toast.success('Admin settings saved');
    } catch {
      // createSimpleMutation already surfaced the error toast
    }
  });

  async function handleTest() {
    testMutation.reset();
    try {
      await testMutation.mutateAsync(formToTestPayload(getValues()));
    } catch {
      // createSimpleMutation already surfaced the error toast
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
                value={field.value as AiProvider}
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
              control={control}
              setValue={setValue}
            />
          )}
          {provider === AiProvider.Claude && (
            <AnthropicFields
              register={register}
              errors={errors}
              apiKeySet={settings?.llmApiKeySet ?? false}
              control={control}
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
  provider: AiProvider,
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

