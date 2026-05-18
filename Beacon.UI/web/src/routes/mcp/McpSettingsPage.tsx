import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { useQuery } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import {
  PageHeader,
  Button,
  Card,
  Field,
  Input,
  Select,
  Textarea,
} from '@/components/beacon';
import { Tabs } from '@/components/Tabs';
import { useIsAdmin } from '@/auth/useAuth';
import { beaconApi } from '@/api/client';
import { useProjectsQuery } from '@/routes/projects/queries';
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
type TabKey = 'prompt' | 'tools' | 'guardrails' | 'context';

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
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="signal" emphasis="MCP settings" sub={<span className="text-text-muted">Loading…</span>} />
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
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="signal" emphasis="MCP settings" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="signal" emphasis="MCP settings" sub="Failed to load settings." />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      <form onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4">
        <PageHeader
          variant="signal"
          eyebrow="MCP"
          prefix="Configuring"
          emphasis="MCP settings"
          sub="Configure the Model Context Protocol server behavior, tool descriptions, and guardrails."
          actions={
            <Button
              variant="primary"
              type="submit"
              disabled={updateMutation.isPending}
            >
              {updateMutation.isPending ? 'Saving…' : 'Save settings'}
            </Button>
          }
        />

        <Tabs<TabKey>
          active={tab}
          onChange={setTab}
          tabs={[
            { key: 'prompt', label: 'Pre-prompt' },
            { key: 'tools', label: 'Tool descriptions' },
            { key: 'guardrails', label: 'Guardrails' },
            { key: 'context', label: 'Context preview' },
          ]}
        />

        <Card className="p-4">
          {tab === 'prompt' && (
            <div className="flex flex-col gap-3">
              <p className="text-text-muted text-sm">
                The Ask system prompt controls how the AI generates SQL from natural language. The
                global instruction is prepended to every tool's user context.
              </p>
              <Field label="Ask tool system prompt">
                <Textarea
                  rows={8}
                  {...form.register('askSystemPrompt')}
                  placeholder="Leave blank to use the default system prompt."
                />
              </Field>
              <Field label="Global instruction">
                <Textarea
                  rows={4}
                  {...form.register('globalInstruction')}
                  placeholder="Prepended to user messages in all LLM-aware tools."
                />
              </Field>
            </div>
          )}

          {tab === 'tools' && (
            <div className="flex flex-col gap-3">
              <p className="text-text-muted text-sm">
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
            <div className="flex flex-col gap-3">
              <Field label="Max row limit">
                <Input
                  type="number"
                  min={1}
                  max={100000}
                  {...form.register('maxRowLimit', { valueAsNumber: true })}
                />
              </Field>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('enforceReadOnly')} />
                Enforce read-only queries
              </label>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('enablePiiDetection')} />
                Enable PII detection
              </label>
              <Field label="Custom PII patterns (one per line)">
                <Textarea
                  rows={6}
                  {...form.register('customPiiPatternsText')}
                  placeholder={'customer_name\naccount_number\n\\biban\\b'}
                />
              </Field>

              <hr className="my-2 border-0 border-t border-border" />
              <h3 className="m-0 text-sm font-semibold text-text">Learning</h3>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('enableLearning')} />
                Enable learning
              </label>
              <Field label="Auto-approve threshold (0–1)">
                <Input
                  type="number"
                  step={0.05}
                  min={0}
                  max={1}
                  {...form.register('learningAutoApproveThreshold', { valueAsNumber: true })}
                />
              </Field>
              <Field label="Injection budget (chars)">
                <Input
                  type="number"
                  min={0}
                  {...form.register('learningInjectionBudgetChars', { valueAsNumber: true })}
                />
              </Field>
              <Field label="Signal retention (days)">
                <Input
                  type="number"
                  min={0}
                  {...form.register('learningSignalRetentionDays', { valueAsNumber: true })}
                />
              </Field>
            </div>
          )}

          {tab === 'context' && <ProjectContextPreview />}
        </Card>
      </form>
    </div>
  );
}

function ProjectContextPreview() {
  const projectsQuery = useProjectsQuery();
  const [selectedProjectId, setSelectedProjectId] = useState<number | undefined>(undefined);

  const contextQuery = useQuery({
    queryKey: ['project-mcp-context', selectedProjectId],
    queryFn: () =>
      beaconApi().getProjectMcpContext(selectedProjectId as number) as unknown as Promise<{ context: string }>,
    enabled: selectedProjectId !== undefined,
  });

  const projects = projectsQuery.data?.entries ?? [];

  return (
    <div className="flex flex-col gap-3">
      <p className="text-text-muted text-sm">
        Preview the knowledge-graph context that Beacon injects into MCP tool calls for a selected project.
      </p>
      <Field label="Project">
        <Select
          value={selectedProjectId ?? ''}
          onChange={e => setSelectedProjectId(e.target.value ? Number(e.target.value) : undefined)}
        >
          <option value="">— Select project —</option>
          {projects.map(p => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </Select>
      </Field>

      {contextQuery.isLoading && <div className="text-text-muted">Loading context…</div>}
      {contextQuery.isError && (
        <div className="text-xs text-crit">Failed to load context: {contextQuery.error instanceof Error ? contextQuery.error.message : 'unknown error'}</div>
      )}
      {contextQuery.data && (
        <pre className="mono bg-surface-2 border border-border rounded-md p-4 text-xs leading-relaxed overflow-x-auto max-h-[500px] whitespace-pre-wrap break-words m-0">
          {contextQuery.data.context || '(empty context)'}
        </pre>
      )}
      {!contextQuery.isLoading && !contextQuery.isError && !contextQuery.data && selectedProjectId && (
        <div className="text-text-muted">No context available for this project.</div>
      )}
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
    <Field label={label}>
      <Textarea rows={3} {...form.register(name)} placeholder="(default)" />
    </Field>
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
