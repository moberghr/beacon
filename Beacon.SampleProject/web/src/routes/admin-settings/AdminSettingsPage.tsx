import { useEffect, useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { ApiError } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { useIsAdmin } from '@/auth/useAuth';
import {
  AI_PROVIDER_LABEL,
  AiProvider,
  useAdminSettingsQuery,
  useUpdateAdminSettings,
  type AdminSettingsView,
} from './queries';

const SCHEMA = z.object({
  baseUrl: z.string().trim().max(500).optional(),
  llmProvider: z.number().int().min(0).max(3),
  llmModel: z.string().trim().max(200).optional(),
  llmFastModel: z.string().trim().max(200).optional(),
  llmRegion: z.string().trim().max(100).optional(),
  // Empty string = "leave secret as is". Non-empty replaces it.
  llmApiKey: z.string().max(500).optional(),
  llmEndpoint: z.string().trim().max(500).optional(),
  llmSessionToken: z.string().max(2000).optional(),
  llmMaxConcurrentRequests: z.number().int().min(1).max(1000),
  llmTokensPerMinute: z.number().int().min(0),
  llmRequestsPerMinute: z.number().int().min(0),
  llmMonthlyBudget: z.number().min(0),
});

type FormValues = z.infer<typeof SCHEMA>;

function emptyToNull(value: string | undefined): string | null {
  if (value === undefined) return null;
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

export default function AdminSettingsPage() {
  const isAdmin = useIsAdmin();
  const navigate = useNavigate();

  // Route-level admin gate. While auth is loading isAdmin is undefined; render
  // a loading shell rather than redirecting prematurely.
  useEffect(() => {
    if (isAdmin === false) {
      toast.error('Admin role required.');
      navigate('/home', { replace: true });
    }
  }, [isAdmin, navigate]);

  if (isAdmin === undefined) {
    return (
      <div className="page">
        <PageHeader title="Admin settings" sub={<span className="muted">Loading…</span>} />
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

  const settings = data?.settings;
  const history = data?.history ?? [];

  const defaults = useMemo<FormValues>(() => settingsToForm(settings), [settings]);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting, isDirty },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: defaults,
  });

  useEffect(() => {
    reset(defaults);
  }, [defaults, reset]);

  const onSubmit = handleSubmit(async values => {
    try {
      await updateMutation.mutateAsync({
        baseUrl: emptyToNull(values.baseUrl),
        llmProvider: values.llmProvider as 0 | 1 | 2 | 3,
        llmModel: emptyToNull(values.llmModel),
        llmFastModel: emptyToNull(values.llmFastModel),
        llmRegion: emptyToNull(values.llmRegion),
        // Empty string = leave existing secret. Non-empty = replace.
        llmApiKey: values.llmApiKey && values.llmApiKey.length > 0 ? values.llmApiKey : null,
        llmEndpoint: emptyToNull(values.llmEndpoint),
        llmSessionToken: values.llmSessionToken && values.llmSessionToken.length > 0 ? values.llmSessionToken : null,
        llmMaxConcurrentRequests: values.llmMaxConcurrentRequests,
        llmTokensPerMinute: values.llmTokensPerMinute,
        llmRequestsPerMinute: values.llmRequestsPerMinute,
        llmMonthlyBudget: values.llmMonthlyBudget,
      });
      toast.success('Admin settings saved');
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Save failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  if (isLoading) {
    return (
      <div className="page">
        <PageHeader title="Admin settings" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <PageHeader title="Admin settings" />
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load settings"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  return (
    <div className="page">
      <PageHeader
        title="Admin settings"
        sub={<span className="muted">System-wide configuration. Changes take effect immediately.</span>}
      />

      <form onSubmit={onSubmit} noValidate style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <Section title="General" sub="Base URL the LLM and integrations should use to reach this Beacon instance.">
          <Field label="Base URL" error={errors.baseUrl?.message}>
            <input className="q-input" type="url" placeholder="https://beacon.example.com" {...register('baseUrl')} />
          </Field>
        </Section>

        <Section title="LLM provider" sub="Which large-language-model provider Beacon uses for AI features.">
          <Field label="Provider" error={errors.llmProvider?.message}>
            <select className="q-select" {...register('llmProvider', { valueAsNumber: true })}>
              {Object.entries(AI_PROVIDER_LABEL).map(([id, label]) => (
                <option key={id} value={id}>{label}</option>
              ))}
            </select>
          </Field>
          <Field label="Model" error={errors.llmModel?.message}>
            <input className="q-input mono" type="text" placeholder="gpt-4o" {...register('llmModel')} />
          </Field>
          <Field label="Fast model" error={errors.llmFastModel?.message}>
            <input className="q-input mono" type="text" placeholder="gpt-4o-mini" {...register('llmFastModel')} />
          </Field>
          <Field label="Region" error={errors.llmRegion?.message}>
            <input className="q-input" type="text" placeholder="us-east-1 (Bedrock) or eastus (Azure OpenAI)" {...register('llmRegion')} />
          </Field>
        </Section>

        <Section
          title="LLM credentials"
          sub="Secrets are write-only — leave blank to keep the existing value, type a new value to replace it."
        >
          <Field label="API key">
            <input
              type="password"
              className="q-input"
              autoComplete="new-password"
              placeholder={settings?.llmApiKeySet ? 'Set — leave blank to keep' : 'Not set'}
              {...register('llmApiKey')}
            />
          </Field>
          <Field label="Endpoint">
            <input
              type="password"
              className="q-input"
              autoComplete="new-password"
              placeholder={settings?.llmEndpointSet ? 'Set — leave blank to keep' : 'Not set (used for Azure OpenAI)'}
              {...register('llmEndpoint')}
            />
          </Field>
          <Field label="Session token">
            <input
              type="password"
              className="q-input"
              autoComplete="new-password"
              placeholder={settings?.llmSessionTokenSet ? 'Set — leave blank to keep' : 'Not set (used for Bedrock STS)'}
              {...register('llmSessionToken')}
            />
          </Field>
        </Section>

        <Section title="Rate limits & budget" sub="Throttle LLM calls so a runaway query doesn't drain the budget.">
          <Field label="Max concurrent requests" error={errors.llmMaxConcurrentRequests?.message}>
            <input className="q-input" type="number" min={1} {...register('llmMaxConcurrentRequests', { valueAsNumber: true })} />
          </Field>
          <Field label="Tokens per minute" error={errors.llmTokensPerMinute?.message}>
            <input className="q-input" type="number" min={0} {...register('llmTokensPerMinute', { valueAsNumber: true })} />
          </Field>
          <Field label="Requests per minute" error={errors.llmRequestsPerMinute?.message}>
            <input className="q-input" type="number" min={0} {...register('llmRequestsPerMinute', { valueAsNumber: true })} />
          </Field>
          <Field label="Monthly budget (USD)" error={errors.llmMonthlyBudget?.message}>
            <input className="q-input" type="number" min={0} step="0.01" {...register('llmMonthlyBudget', { valueAsNumber: true })} />
          </Field>
        </Section>

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button
            type="button"
            className="btn"
            onClick={() => reset(defaults)}
            disabled={!isDirty || isSubmitting}
          >
            Reset
          </button>
          <button
            type="submit"
            className="btn btn--primary"
            disabled={!isDirty || isSubmitting}
          >
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </button>
        </div>
      </form>

      {history.length > 0 && (
        <div className="card" style={{ marginTop: 24 }}>
          <div className="card__body">
            <h3 className="card__title" style={{ margin: '0 0 8px' }}>Recent changes</h3>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              {history.slice(0, 20).map((h, i) => (
                <div key={i} style={{ fontSize: 12 }}>
                  <span className="muted mono">{formatDateTime(h.changedAt)}</span>
                  <span style={{ margin: '0 6px' }}>·</span>
                  <span className="mono">{h.settingKey}</span>
                  <span className="muted" style={{ margin: '0 6px' }}>by</span>
                  <span>{h.changedByUserId ?? 'system'}</span>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function settingsToForm(s: AdminSettingsView | undefined): FormValues {
  if (s === undefined) {
    return {
      baseUrl: '',
      llmProvider: AiProvider.OpenAI,
      llmModel: '',
      llmFastModel: '',
      llmRegion: '',
      llmApiKey: '',
      llmEndpoint: '',
      llmSessionToken: '',
      llmMaxConcurrentRequests: 5,
      llmTokensPerMinute: 0,
      llmRequestsPerMinute: 0,
      llmMonthlyBudget: 0,
    };
  }
  return {
    baseUrl: s.baseUrl ?? '',
    llmProvider: (s.llmProvider ?? AiProvider.OpenAI) as 0 | 1 | 2 | 3,
    llmModel: s.llmModel ?? '',
    llmFastModel: s.llmFastModel ?? '',
    llmRegion: s.llmRegion ?? '',
    llmApiKey: '',
    llmEndpoint: '',
    llmSessionToken: '',
    llmMaxConcurrentRequests: s.llmMaxConcurrentRequests,
    llmTokensPerMinute: s.llmTokensPerMinute,
    llmRequestsPerMinute: s.llmRequestsPerMinute,
    llmMonthlyBudget: s.llmMonthlyBudget,
  };
}

function Section({ title, sub, children }: {
  title: string;
  sub?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="card">
      <div className="card__body">
        <h3 className="card__title" style={{ margin: '0 0 4px' }}>{title}</h3>
        {sub && <div className="muted" style={{ fontSize: 12, marginBottom: 12 }}>{sub}</div>}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {children}
        </div>
      </div>
    </div>
  );
}

function Field({ label, error, children }: {
  label: string;
  error?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="q-field">
      <label className="q-label">{label}</label>
      {children}
      {error && <div className="q-error">{error}</div>}
    </div>
  );
}

