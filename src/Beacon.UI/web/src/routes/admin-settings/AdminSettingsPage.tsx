import { useEffect, useMemo } from 'react';
import { useForm, Controller, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { toast } from 'sonner';
import { AlertTriangle, Eye, Lock } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { Banner, Button, Card, CardBody, Field, Input, PageHeader } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import { useAccess } from '@/auth/useAuth';
import { useRequirePermission } from '@/auth/useRequirePermission';
import { AiProvider, ChangeApprovalKind, type BedrockAuthMode } from '@/lib/enums';
import { AwaitingApprovalBanner } from '@/routes/approvals/AwaitingApproval';
import { useChangeApprovalsList } from '@/routes/approvals/queries';
import {
  modelsForProvider,
  providerRequiresApiKey,
  useAdminSettingsQuery,
  useLlmProvidersQuery,
  useUpdateAdminSettings,
  useTestLlmConnection,
} from './queries';
import {
  ADMIN_SETTINGS_SCHEMA,
  settingsToForm,
  formToUpdatePayload,
  formToTestPayload,
  keepStoredConnection,
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

/**
 * Read by an Auditor or a SecurityAdmin, changed by a SecurityAdmin, except the LLM provider, endpoint, region,
 * authentication mode and credentials: those stay an Admin's.
 */
export default function AdminSettingsPage() {
  const allowed = useRequirePermission('Auditor', 'SecurityAdmin');

  if (allowed === undefined) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="System" prefix="Admin" emphasis="settings" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (allowed === false) {
    return null;
  }

  return <AdminSettingsForm />;
}

/** Only a live Admin reads the LLM connection's change requests, so only an Admin renders (and asks for) this. */
function PendingLlmChangeBanner() {
  const { items } = useChangeApprovalsList(ChangeApprovalKind.LlmConnection);
  const pending = items[0];

  if (!pending) {
    return null;
  }

  return (
    <AwaitingApprovalBanner
      kind={ChangeApprovalKind.LlmConnection}
      title="A change of the LLM connection waits for approval"
      sub="The stored connection stays in use until a second person approves the change. Saving the settings again replaces the pending request."
      requestId={pending.id}
    />
  );
}

function AdminSettingsForm() {
  const access = useAccess();
  const canEdit = access.hasPermission('SecurityAdmin');
  const connectionLocked = !access.isAdmin;
  const { data, isLoading, isError, error } = useAdminSettingsQuery();
  const { data: providerCatalog, isPending: providersLoading, isError: providersFailed } = useLlmProvidersQuery();
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
    setError,
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
  const rateLimitsEnabled = useWatch({ control, name: 'llmRateLimitsEnabled' });
  // The stored API key belongs to the stored provider: another one is never offered "leave blank to keep".
  const storedApiKeyApplies = (settings?.llmApiKeySet ?? false) && provider === settings?.llmProvider;

  const onSubmit = handleSubmit(async values => {
    if (!canEdit) return;
    // Only an Admin supplies credentials. Switching to a provider that needs a key takes a typed one (the stored key
    // belongs to the previous provider); keeping the provider never asks, so other settings save without an LLM key.
    // Compared with the form's default, so a picker left on it while no provider is stored is not a switch.
    const nextProvider = values.llmProvider as AiProvider;
    const providerChanged = nextProvider !== defaults.llmProvider;
    const apiKeyMissing = !connectionLocked
      && providerChanged
      && providerRequiresApiKey(providerCatalog, nextProvider)
      && !values.llmApiKey;
    if (apiKeyMissing) {
      setError('llmApiKey', { type: 'required', message: 'This provider needs an API key.' });
      return;
    }
    const payload = formToUpdatePayload(values);
    try {
      // The base URL, like the LLM connection, is an Admin's to change: anyone else sends the stored one back.
      await updateMutation.mutateAsync(
        connectionLocked ? { ...keepStoredConnection(payload, settings), baseUrl: settings?.baseUrl ?? payload.baseUrl } : payload,
      );
      // The PUT answers 204 whether a change of the LLM connection was applied or staged, so the toast claims neither;
      // the pending banner (its approvals query is invalidated by the save) shows a staged one.
      toast.success(
        connectionLocked
          ? 'Admin settings saved'
          : 'Admin settings saved. If you changed the LLM provider, endpoint, region or authentication mode, that change was submitted for approval: the banner above shows it while it waits.',
      );
    } catch {
      // createSimpleMutation already surfaced the error toast
    }
  });

  async function handleTest() {
    testMutation.reset();
    const payload = formToTestPayload(getValues());
    try {
      await testMutation.mutateAsync(connectionLocked ? keepStoredConnection(payload, settings) : payload);
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

      {access.isAdmin && <PendingLlmChangeBanner />}

      {!canEdit && (
        <Banner
          tone="info"
          icon={<Eye />}
          title="Read only"
          sub="You can read these settings; secrets show only whether they are set. Changing them needs the SecurityAdmin permission."
        />
      )}

      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <fieldset disabled={!canEdit} className="m-0 p-0 border-0 min-w-0 flex flex-col gap-4">
        <Section
          title="General"
          sub={
            connectionLocked
              ? 'Base URL the LLM and integrations should use to reach this Beacon instance. Only an Admin changes it.'
              : 'Base URL the LLM and integrations should use to reach this Beacon instance.'
          }
        >
          <Field label="Base URL" hint={errors.baseUrl?.message ? <span className="text-crit">{errors.baseUrl.message}</span> : undefined}>
            <Input type="url" placeholder="https://beacon.example.com" disabled={connectionLocked} {...register('baseUrl')} />
          </Field>
        </Section>

        <Section
          title="AI provider"
          sub="Pick the large-language-model provider. Only the fields the provider needs are shown."
        >
          {providersFailed && (
            <Banner
              tone="warn"
              icon={<AlertTriangle />}
              title="The installed providers could not be loaded"
              sub="Every provider is offered; saving one this host has not installed is refused."
            />
          )}

          {canEdit && connectionLocked && (
            <Banner
              tone="info"
              icon={<Lock />}
              title="The LLM connection is an Admin's to change"
              sub="Only an Admin changes the provider, endpoint, region, authentication mode or credentials, and the concurrency and per-minute limits. You can change the models, the other settings, and test the stored connection."
            />
          )}

          <Controller
            control={control}
            name="llmProvider"
            render={({ field }) => (
              <ProviderPicker
                disabled={connectionLocked}
                catalog={providerCatalog}
                storedProvider={settings?.llmProvider ?? null}
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
              modelKnown={storedApiKeyApplies}
              control={control}
              setValue={setValue}
              locked={connectionLocked}
            />
          )}
          {provider === AiProvider.Claude && (
            <AnthropicFields
              register={register}
              errors={errors}
              apiKeySet={storedApiKeyApplies}
              control={control}
              setValue={setValue}
              locked={connectionLocked}
            />
          )}
          {provider === AiProvider.AzureOpenAI && (
            <AzureFields
              register={register}
              errors={errors}
              apiKeySet={storedApiKeyApplies}
              endpointSet={settings?.llmEndpointSet ?? false}
              locked={connectionLocked}
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
              locked={connectionLocked}
            />
          )}

          {canEdit && (
            <>
              <div className="border-t border-border my-3" />

              <TestConnectionRow
                onTest={handleTest}
                pending={testMutation.isPending}
                result={testMutation.data}
                error={testMutation.error}
                hint={
                  connectionLocked
                    ? 'Sends a 1-token ping with the stored connection and the models on this form (without saving).'
                    : undefined
                }
              />
            </>
          )}
        </Section>

        <Section
          title="Rate limits & budget"
          sub="The concurrency limit applies as soon as you save. Requests and tokens per minute apply only while enforced; 0 means unlimited."
        >
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Field label="Max concurrent requests" hint={errors.llmMaxConcurrentRequests?.message ? <span className="text-crit">{errors.llmMaxConcurrentRequests.message}</span> : undefined}>
              <Input type="number" min={1} disabled={connectionLocked} {...register('llmMaxConcurrentRequests', { valueAsNumber: true })} />
            </Field>
            <label className="flex gap-2 items-center text-sm md:col-span-2">
              <input type="checkbox" disabled={connectionLocked} {...register('llmRateLimitsEnabled')} />
              Enforce requests/tokens per minute
            </label>
            <Field label="Tokens per minute" hint={rateLimitHint(errors.llmTokensPerMinute?.message, rateLimitsEnabled)}>
              <Input type="number" min={0} disabled={connectionLocked || !rateLimitsEnabled} {...register('llmTokensPerMinute', { valueAsNumber: true })} />
            </Field>
            <Field label="Requests per minute" hint={rateLimitHint(errors.llmRequestsPerMinute?.message, rateLimitsEnabled)}>
              <Input type="number" min={0} disabled={connectionLocked || !rateLimitsEnabled} {...register('llmRequestsPerMinute', { valueAsNumber: true })} />
            </Field>
            <Field
              label="Monthly budget (USD)"
              hint={
                errors.llmMonthlyBudget?.message ? (
                  <span className="text-crit">{errors.llmMonthlyBudget.message}</span>
                ) : (
                  'Recorded, not enforced: Beacon does not track spend, so calls continue past this amount.'
                )
              }
            >
              <Input type="number" min={0} step="0.01" {...register('llmMonthlyBudget', { valueAsNumber: true })} />
            </Field>
          </div>
        </Section>

        </fieldset>

        {canEdit && (
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
              disabled={!isDirty || isSubmitting || providersLoading}
            >
              {isSubmitting ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        )}
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

/** A per-minute limit's hint: its validation error, else a note that it is not applied while enforcement is off. */
function rateLimitHint(error: string | undefined, enforced: boolean) {
  if (error) {
    return <span className="text-crit">{error}</span>;
  }

  return enforced ? undefined : 'Recorded, not enforced: turn on enforcement to apply it.';
}
