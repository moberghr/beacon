import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { PageHeader } from '@/components/layout/PageHeader';
import { Tabs } from '@/components/Tabs';
import { useIsAdmin } from '@/auth/useAuth';
import {
  describeMcpError,
  useMcpSettings,
  useUpdateMcpSettings,
  type McpSettingsData,
} from './queries';

const SCHEMA = z.object({
  askSystemPrompt: z.string().nullable(),
  globalInstruction: z.string().nullable(),
  getContextDescription: z.string().nullable(),
  queryDescription: z.string().nullable(),
  getDocumentationDescription: z.string().nullable(),
  askDescription: z.string().nullable(),
  searchDescription: z.string().nullable(),
  maxRowLimit: z.number().int().min(1).max(100000),
  enforceReadOnly: z.boolean(),
  enablePiiDetection: z.boolean(),
  customPiiPatternsText: z.string(),
  enableLearning: z.boolean(),
  learningAutoApproveThreshold: z.number().min(0).max(1),
  learningInjectionBudgetChars: z.number().int().min(0),
  learningSignalRetentionDays: z.number().int().min(0),
});

type FormValues = z.infer<typeof SCHEMA>;
type TabKey = 'prompt' | 'tools' | 'guardrails';

export default function McpSettingsPage() {
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
      <div className="page">
        <PageHeader title="MCP settings" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }
  if (isAdmin === false) return null;

  return <McpSettingsForm />;
}

function McpSettingsForm() {
  const { data, isLoading, isError } = useMcpSettings();
  const updateMutation = useUpdateMcpSettings();
  const [tab, setTab] = useState<TabKey>('prompt');

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: settingsToForm(data),
  });

  useEffect(() => {
    if (data) form.reset(settingsToForm(data));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data]);

  function onSubmit(values: FormValues) {
    const payload: McpSettingsData = {
      askSystemPrompt: values.askSystemPrompt,
      globalInstruction: values.globalInstruction,
      getContextDescription: values.getContextDescription,
      queryDescription: values.queryDescription,
      getDocumentationDescription: values.getDocumentationDescription,
      askDescription: values.askDescription,
      searchDescription: values.searchDescription,
      maxRowLimit: values.maxRowLimit,
      enforceReadOnly: values.enforceReadOnly,
      enablePiiDetection: values.enablePiiDetection,
      customPiiPatterns: values.customPiiPatternsText
        .split('\n')
        .map(s => s.trim())
        .filter(Boolean),
      enableLearning: values.enableLearning,
      learningAutoApproveThreshold: values.learningAutoApproveThreshold,
      learningInjectionBudgetChars: values.learningInjectionBudgetChars,
      learningSignalRetentionDays: values.learningSignalRetentionDays,
    };
    updateMutation.mutate(payload, {
      onSuccess: () => toast.success('MCP settings saved.'),
      onError: err => toast.error(describeMcpError(err, 'Save failed')),
    });
  }

  if (isLoading) {
    return (
      <div className="page">
        <PageHeader title="MCP settings" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <PageHeader title="MCP settings" sub="Failed to load settings." />
      </div>
    );
  }

  return (
    <div className="page">
      <form onSubmit={form.handleSubmit(onSubmit)}>
        <PageHeader
          title="MCP settings"
          sub="Configure the Model Context Protocol server behavior, tool descriptions, and guardrails."
          actions={
            <button
              type="submit"
              className="btn btn--primary"
              disabled={updateMutation.isPending}
            >
              {updateMutation.isPending ? 'Saving…' : 'Save settings'}
            </button>
          }
        />

        <Tabs<TabKey>
          active={tab}
          onChange={setTab}
          tabs={[
            { key: 'prompt', label: 'Pre-prompt' },
            { key: 'tools', label: 'Tool descriptions' },
            { key: 'guardrails', label: 'Guardrails' },
          ]}
        />

        <div className="card" style={{ padding: 16, marginTop: 12 }}>
          {tab === 'prompt' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <p className="muted">
                The Ask system prompt controls how the AI generates SQL from natural language. The
                global instruction is prepended to every tool's user context.
              </p>
              <label className="field">
                <span className="field__label">Ask tool system prompt</span>
                <textarea
                  className="textarea"
                  rows={8}
                  {...form.register('askSystemPrompt')}
                  placeholder="Leave blank to use the default system prompt."
                />
              </label>
              <label className="field">
                <span className="field__label">Global instruction</span>
                <textarea
                  className="textarea"
                  rows={4}
                  {...form.register('globalInstruction')}
                  placeholder="Prepended to user messages in all LLM-aware tools."
                />
              </label>
            </div>
          )}

          {tab === 'tools' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <p className="muted">
                Override tool descriptions returned by tools/list. Leave blank to use defaults.
              </p>
              <ToolField label="get_context" name="getContextDescription" form={form} />
              <ToolField label="ask" name="askDescription" form={form} />
              <ToolField label="query" name="queryDescription" form={form} />
              <ToolField label="get_documentation" name="getDocumentationDescription" form={form} />
              <ToolField label="search" name="searchDescription" form={form} />
            </div>
          )}

          {tab === 'guardrails' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <label className="field">
                <span className="field__label">Max row limit</span>
                <input
                  className="input"
                  type="number"
                  min={1}
                  max={100000}
                  {...form.register('maxRowLimit', { valueAsNumber: true })}
                />
              </label>
              <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input type="checkbox" {...form.register('enforceReadOnly')} />
                Enforce read-only queries
              </label>
              <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input type="checkbox" {...form.register('enablePiiDetection')} />
                Enable PII detection
              </label>
              <label className="field">
                <span className="field__label">Custom PII patterns (one per line)</span>
                <textarea
                  className="textarea"
                  rows={6}
                  {...form.register('customPiiPatternsText')}
                  placeholder={'customer_name\naccount_number\n\\biban\\b'}
                />
              </label>

              <hr style={{ margin: '8px 0', border: 0, borderTop: '1px solid var(--border)' }} />
              <h3 style={{ margin: 0 }}>Learning</h3>
              <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input type="checkbox" {...form.register('enableLearning')} />
                Enable learning
              </label>
              <label className="field">
                <span className="field__label">Auto-approve threshold (0–1)</span>
                <input
                  className="input"
                  type="number"
                  step={0.05}
                  min={0}
                  max={1}
                  {...form.register('learningAutoApproveThreshold', { valueAsNumber: true })}
                />
              </label>
              <label className="field">
                <span className="field__label">Injection budget (chars)</span>
                <input
                  className="input"
                  type="number"
                  min={0}
                  {...form.register('learningInjectionBudgetChars', { valueAsNumber: true })}
                />
              </label>
              <label className="field">
                <span className="field__label">Signal retention (days)</span>
                <input
                  className="input"
                  type="number"
                  min={0}
                  {...form.register('learningSignalRetentionDays', { valueAsNumber: true })}
                />
              </label>
            </div>
          )}
        </div>

        <p className="muted" style={{ marginTop: 12, fontSize: 12 }}>
          Note: the legacy Blazor page also includes a per-project context preview powered by the
          Beacon.AI knowledge graph service. That tab is not yet ported — use the legacy{' '}
          <a href="/beacon/settings/mcp">/beacon/settings/mcp</a> page for context preview.
        </p>
      </form>
    </div>
  );
}

interface ToolFieldProps {
  label: string;
  name:
    | 'getContextDescription'
    | 'askDescription'
    | 'queryDescription'
    | 'getDocumentationDescription'
    | 'searchDescription';
  form: ReturnType<typeof useForm<FormValues>>;
}

function ToolField({ label, name, form }: ToolFieldProps) {
  return (
    <label className="field">
      <span className="field__label">{label}</span>
      <textarea className="textarea" rows={3} {...form.register(name)} placeholder="(default)" />
    </label>
  );
}

function settingsToForm(data: McpSettingsData | undefined): FormValues {
  return {
    askSystemPrompt: data?.askSystemPrompt ?? '',
    globalInstruction: data?.globalInstruction ?? '',
    getContextDescription: data?.getContextDescription ?? '',
    queryDescription: data?.queryDescription ?? '',
    getDocumentationDescription: data?.getDocumentationDescription ?? '',
    askDescription: data?.askDescription ?? '',
    searchDescription: data?.searchDescription ?? '',
    maxRowLimit: data?.maxRowLimit ?? 1000,
    enforceReadOnly: data?.enforceReadOnly ?? true,
    enablePiiDetection: data?.enablePiiDetection ?? true,
    customPiiPatternsText: (data?.customPiiPatterns ?? []).join('\n'),
    enableLearning: data?.enableLearning ?? true,
    learningAutoApproveThreshold: data?.learningAutoApproveThreshold ?? 0.85,
    learningInjectionBudgetChars: data?.learningInjectionBudgetChars ?? 4000,
    learningSignalRetentionDays: data?.learningSignalRetentionDays ?? 90,
  };
}
