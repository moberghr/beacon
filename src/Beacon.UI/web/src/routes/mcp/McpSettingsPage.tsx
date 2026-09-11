import { useEffect, useState, type ReactNode } from 'react';
import { useForm, type FieldErrors } from 'react-hook-form';
import { useQuery } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Lock, ShieldAlert } from 'lucide-react';
import {
  PageHeader,
  Banner,
  Button,
  Card,
  Field,
  Input,
  Select,
  Textarea,
} from '@/components/beacon';
import { Tabs } from '@/components/Tabs';
import { useRequireAdmin } from '@/auth/useRequireAdmin';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { useProjectsQuery } from '@/routes/projects/queries';
import {
  useMcpProjectSettings,
  useMcpSettings,
  useUpdateMcpProjectSettings,
  useUpdateMcpSettings,
  type McpProjectSettingsData,
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
  retainQueryContent: z.boolean(),
  statementTimeoutSeconds: z.number().int().min(1),
  maxResultBytes: z.number().int().min(1),
  // Blank = no EXPLAIN cost limit (null on the wire); otherwise a non-negative number.
  maxExplainCostText: z
    .string()
    .refine(s => s.trim() === '' || (Number.isFinite(Number(s)) && Number(s) >= 0), {
      message: 'Leave blank for no limit, or enter a number ≥ 0.',
    }),
  maxConcurrentQueriesPerKey: z.number().int().min(1),
  allowExplicitFeedbackContent: z.boolean(),
});

type FormValues = z.infer<typeof SCHEMA>;
type TabKey = 'prompt' | 'tools' | 'guardrails' | 'context';
type Scope = 'global' | number;

// Which tab each form field lives on — used to surface the tab containing the
// first validation error on a failed submit (errors on a hidden tab would
// otherwise make Save look silently dead).
export const FIELD_TAB: Record<keyof FormValues, TabKey> = {
  askSystemPrompt: 'prompt',
  globalInstruction: 'prompt',
  getContextDescription: 'tools',
  queryDescription: 'tools',
  getDocumentationDescription: 'tools',
  askDescription: 'tools',
  searchDescription: 'tools',
  maxRowLimit: 'guardrails',
  enforceReadOnly: 'guardrails',
  enablePiiDetection: 'guardrails',
  customPiiPatternsText: 'guardrails',
  enableLearning: 'guardrails',
  learningAutoApproveThreshold: 'guardrails',
  learningInjectionBudgetChars: 'guardrails',
  learningSignalRetentionDays: 'guardrails',
  retainQueryContent: 'guardrails',
  statementTimeoutSeconds: 'guardrails',
  maxResultBytes: 'guardrails',
  maxExplainCostText: 'guardrails',
  maxConcurrentQueriesPerKey: 'guardrails',
  allowExplicitFeedbackContent: 'guardrails',
};

export default function McpSettingsPage() {
  const isAdmin = useRequireAdmin();

  if (isAdmin === undefined) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="signal"
          emphasis="MCP settings"
          sub={<span className="text-text-muted">Loading…</span>}
        />
      </div>
    );
  }
  if (isAdmin === false) return null;

  return <McpSettingsShell />;
}

/**
 * Owns the scope selector. "Global defaults" edits the single global row; a project
 * edits that project's overrides (null = inherit). The selector rides in each form's
 * header so it stays reachable while a scope is loading or failed to load.
 */
function McpSettingsShell() {
  const projectsQuery = useProjectsQuery();
  const [scope, setScope] = useState<Scope>('global');
  const projects = projectsQuery.data?.entries ?? [];

  const scopeSelector = (
    <Select
      aria-label="Settings scope"
      className="w-auto min-w-[14rem]"
      value={scope === 'global' ? 'global' : String(scope)}
      onChange={e => setScope(e.target.value === 'global' ? 'global' : Number(e.target.value))}
    >
      <option value="global">Global defaults</option>
      {projects.map(p => (
        <option key={p.id} value={p.id}>
          Project: {p.name}
        </option>
      ))}
    </Select>
  );

  if (scope === 'global') {
    return <McpSettingsForm scopeSelector={scopeSelector} />;
  }

  const projectName = projects.find(p => p.id === scope)?.name ?? `#${scope}`;
  return (
    <ProjectSettingsForm
      key={scope}
      projectId={scope}
      projectName={projectName}
      scopeSelector={scopeSelector}
    />
  );
}

function McpSettingsForm({ scopeSelector }: { scopeSelector: ReactNode }) {
  const { data, isLoading, isError } = useMcpSettings();
  const updateMutation = useUpdateMcpSettings();
  const [tab, setTab] = useState<TabKey>('prompt');

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: settingsToForm(data),
  });

  useEffect(() => {
    if (data) form.reset(settingsToForm(data));
  }, [data, form]);

  const { errors } = form.formState;

  function onSubmit(values: FormValues) {
    // Blank/whitespace-only nullable text fields must round-trip back to null so
    // the backend's `?? default` fallback keeps working — saving '' would wipe
    // the default Ask system prompt and tool descriptions.
    const orNull = (s: string | null) => (s && s.trim() !== '' ? s : null);
    const payload: McpSettingsData = {
      askSystemPrompt: orNull(values.askSystemPrompt),
      globalInstruction: orNull(values.globalInstruction),
      getContextDescription: orNull(values.getContextDescription),
      queryDescription: orNull(values.queryDescription),
      getDocumentationDescription: orNull(values.getDocumentationDescription),
      askDescription: orNull(values.askDescription),
      searchDescription: orNull(values.searchDescription),
      maxRowLimit: values.maxRowLimit,
      enforceReadOnly: values.enforceReadOnly,
      enablePiiDetection: values.enablePiiDetection,
      customPiiPatterns: values.customPiiPatternsText
        .split('\n')
        .map(s => s.trim())
        .filter(Boolean),
      // No UI toggle yet — preserve the stored value so saving never silently flips it
      enableSampleValueCollection: data?.enableSampleValueCollection ?? true,
      enableLearning: values.enableLearning,
      learningAutoApproveThreshold: values.learningAutoApproveThreshold,
      learningInjectionBudgetChars: values.learningInjectionBudgetChars,
      learningSignalRetentionDays: values.learningSignalRetentionDays,
      retainQueryContent: values.retainQueryContent,
      statementTimeoutSeconds: values.statementTimeoutSeconds,
      maxResultBytes: values.maxResultBytes,
      maxExplainCost:
        values.maxExplainCostText.trim() === '' ? null : Number(values.maxExplainCostText),
      maxConcurrentQueriesPerKey: values.maxConcurrentQueriesPerKey,
      allowExplicitFeedbackContent: values.allowExplicitFeedbackContent,
    };
    updateMutation.mutate(payload, {
      onSuccess: () => toast.success('MCP settings saved.'),
    });
  }

  function onInvalid(errs: FieldErrors<FormValues>) {
    const firstField = Object.keys(errs)[0] as keyof FormValues | undefined;
    if (firstField && FIELD_TAB[firstField]) {
      setTab(FIELD_TAB[firstField]);
    }
    toast.error('Fix the validation errors before saving.');
  }

  if (isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="signal"
          emphasis="MCP settings"
          sub={<span className="text-text-muted">Loading…</span>}
          actions={scopeSelector}
        />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="signal"
          emphasis="MCP settings"
          sub="Failed to load settings."
          actions={scopeSelector}
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      <form onSubmit={form.handleSubmit(onSubmit, onInvalid)} className="flex flex-col gap-4">
        <PageHeader
          variant="signal"
          eyebrow="MCP"
          prefix="Configuring"
          emphasis="MCP settings"
          sub="Global defaults for the Model Context Protocol server: behavior, tool descriptions, and guardrails. Pick a project to override guardrails per project."
          actions={
            <div className="flex items-center gap-2">
              {scopeSelector}
              <Button variant="primary" type="submit" disabled={updateMutation.isPending}>
                {updateMutation.isPending ? 'Saving…' : 'Save settings'}
              </Button>
            </div>
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
              <Field
                label="Max row limit"
                hint={
                  errors.maxRowLimit?.message ? (
                    <span className="text-crit">{errors.maxRowLimit.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={1}
                  max={100000}
                  aria-invalid={!!errors.maxRowLimit}
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
              <h3 className="m-0 text-sm font-semibold text-text">Execution</h3>
              <p className="m-0 text-text-muted text-xs">
                Deployment-level locks and ceilings (<span className="mono">Beacon:Mcp</span>) win
                over these values; a save that contradicts a lock is refused.
              </p>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('retainQueryContent')} />
                Retain question and SQL text in signals and audit
              </label>
              <Field
                label="Statement timeout (s)"
                hint={
                  errors.statementTimeoutSeconds?.message ? (
                    <span className="text-crit">{errors.statementTimeoutSeconds.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={1}
                  aria-invalid={!!errors.statementTimeoutSeconds}
                  {...form.register('statementTimeoutSeconds', {
                    valueAsNumber: true,
                  })}
                />
              </Field>
              <Field
                label="Max result size (bytes)"
                hint={
                  errors.maxResultBytes?.message ? (
                    <span className="text-crit">{errors.maxResultBytes.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={1}
                  aria-invalid={!!errors.maxResultBytes}
                  {...form.register('maxResultBytes', { valueAsNumber: true })}
                />
              </Field>
              <Field
                label="Max EXPLAIN cost (blank = no limit)"
                hint={
                  errors.maxExplainCostText?.message ? (
                    <span className="text-crit">{errors.maxExplainCostText.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={0}
                  step="any"
                  aria-invalid={!!errors.maxExplainCostText}
                  {...form.register('maxExplainCostText')}
                />
              </Field>
              <Field
                label="Max concurrent queries per API key"
                hint={
                  errors.maxConcurrentQueriesPerKey?.message ? (
                    <span className="text-crit">{errors.maxConcurrentQueriesPerKey.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={1}
                  aria-invalid={!!errors.maxConcurrentQueriesPerKey}
                  {...form.register('maxConcurrentQueriesPerKey', {
                    valueAsNumber: true,
                  })}
                />
              </Field>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('allowExplicitFeedbackContent')} />
                Allow the feedback tool to store question and SQL text
              </label>

              <hr className="my-2 border-0 border-t border-border" />
              <h3 className="m-0 text-sm font-semibold text-text">Learning</h3>
              <label className="flex gap-2 items-center text-sm">
                <input type="checkbox" {...form.register('enableLearning')} />
                Enable learning
              </label>
              <Field
                label="Auto-approve threshold (0–1)"
                hint={
                  errors.learningAutoApproveThreshold?.message ? (
                    <span className="text-crit">{errors.learningAutoApproveThreshold.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  step={0.05}
                  min={0}
                  max={1}
                  aria-invalid={!!errors.learningAutoApproveThreshold}
                  {...form.register('learningAutoApproveThreshold', {
                    valueAsNumber: true,
                  })}
                />
              </Field>
              <Field
                label="Injection budget (chars)"
                hint={
                  errors.learningInjectionBudgetChars?.message ? (
                    <span className="text-crit">{errors.learningInjectionBudgetChars.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={0}
                  aria-invalid={!!errors.learningInjectionBudgetChars}
                  {...form.register('learningInjectionBudgetChars', {
                    valueAsNumber: true,
                  })}
                />
              </Field>
              <Field
                label="Signal retention (days)"
                hint={
                  errors.learningSignalRetentionDays?.message ? (
                    <span className="text-crit">{errors.learningSignalRetentionDays.message}</span>
                  ) : undefined
                }
              >
                <Input
                  type="number"
                  min={0}
                  aria-invalid={!!errors.learningSignalRetentionDays}
                  {...form.register('learningSignalRetentionDays', {
                    valueAsNumber: true,
                  })}
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

// ---------------------------------------------------------------------------
// Per-project overrides
// ---------------------------------------------------------------------------

type OverrideKey = keyof McpProjectSettingsData & keyof McpSettingsData;

interface OverrideFieldDef {
  key: OverrideKey;
  label: string;
  kind: 'bool' | 'int' | 'float' | 'lines';
  min?: number;
  max?: number;
  step?: number | 'any';
}

interface OverrideSection {
  title: string;
  fields: OverrideFieldDef[];
}

// The per-project surface is the Guardrails tab of the global form. Prompts and tool
// descriptions are global by design (spec mcp-project-settings, "Non-goals").
export const OVERRIDE_SECTIONS: OverrideSection[] = [
  {
    title: 'Guardrails',
    fields: [
      {
        key: 'maxRowLimit',
        label: 'Max row limit',
        kind: 'int',
        min: 1,
        max: 100000,
      },
      {
        key: 'enforceReadOnly',
        label: 'Enforce read-only queries',
        kind: 'bool',
      },
      {
        key: 'enablePiiDetection',
        label: 'Enable PII detection',
        kind: 'bool',
      },
      {
        key: 'customPiiPatterns',
        label: 'Custom PII patterns (one per line)',
        kind: 'lines',
      },
      {
        key: 'enableSampleValueCollection',
        label: 'Collect sample values',
        kind: 'bool',
      },
    ],
  },
  {
    title: 'Execution',
    fields: [
      {
        key: 'retainQueryContent',
        label: 'Retain question and SQL text in signals and audit',
        kind: 'bool',
      },
      {
        key: 'statementTimeoutSeconds',
        label: 'Statement timeout (s)',
        kind: 'int',
        min: 1,
      },
      {
        key: 'maxResultBytes',
        label: 'Max result size (bytes)',
        kind: 'int',
        min: 1,
      },
      {
        key: 'maxExplainCost',
        label: 'Max EXPLAIN cost',
        kind: 'float',
        min: 0,
        step: 'any',
      },
      {
        key: 'maxConcurrentQueriesPerKey',
        label: 'Max concurrent queries per API key',
        kind: 'int',
        min: 1,
      },
      {
        key: 'allowExplicitFeedbackContent',
        label: 'Allow the feedback tool to store question and SQL text',
        kind: 'bool',
      },
    ],
  },
  {
    title: 'Learning',
    fields: [
      { key: 'enableLearning', label: 'Enable learning', kind: 'bool' },
      {
        key: 'learningAutoApproveThreshold',
        label: 'Auto-approve threshold (0–1)',
        kind: 'float',
        min: 0,
        max: 1,
        step: 0.05,
      },
      {
        key: 'learningInjectionBudgetChars',
        label: 'Injection budget (chars)',
        kind: 'int',
        min: 0,
      },
      {
        key: 'learningSignalRetentionDays',
        label: 'Signal retention (days)',
        kind: 'int',
        min: 0,
      },
    ],
  },
];

interface ProjectSettingsFormProps {
  projectId: number;
  projectName: string;
  scopeSelector: ReactNode;
}

function ProjectSettingsForm({ projectId, projectName, scopeSelector }: ProjectSettingsFormProps) {
  const { data, isLoading, isError } = useMcpProjectSettings(projectId);
  const updateMutation = useUpdateMcpProjectSettings();
  // Draft of the override row. Fields the UI does not edit ride along untouched so a
  // save never clears an override it cannot show.
  const [draft, setDraft] = useState<McpProjectSettingsData | undefined>(undefined);

  useEffect(() => {
    if (data) setDraft({ ...data.overrides });
  }, [data]);

  const header = (sub: ReactNode, actions?: ReactNode) => (
    <PageHeader
      variant="signal"
      eyebrow="MCP"
      prefix="Overriding for"
      emphasis={projectName}
      sub={sub}
      actions={
        <div className="flex items-center gap-2">
          {scopeSelector}
          {actions}
        </div>
      }
    />
  );

  if (isLoading || (data && !draft)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        {header(<span className="text-text-muted">Loading…</span>)}
      </div>
    );
  }

  if (isError || !data || !draft) {
    return (
      <div className="flex flex-col gap-5 p-7">{header('Failed to load project settings.')}</div>
    );
  }

  // The server reports lock/clamp names in PascalCase (C# property names); the wire DTO is camelCase.
  const locked = new Set(data.lockedFields.map(lowerFirst));
  const clamped = new Set(data.clampedFields.map(lowerFirst));

  function isOverridden(key: OverrideKey): boolean {
    const v = draft?.[key];
    return v !== null && v !== undefined;
  }

  function setOverride(key: OverrideKey, value: McpProjectSettingsData[OverrideKey]) {
    setDraft(prev => ({ ...(prev ?? {}), [key]: value }));
  }

  function toggleOverride(field: OverrideFieldDef, on: boolean) {
    // Turning an override on seeds it with the effective value so the row shows what
    // the project will keep getting until the admin changes it. A null effective value
    // (e.g. no EXPLAIN cost limit) would read as "inherit" again, so seed a kind-appropriate
    // placeholder the admin then edits.
    const effective = data!.effective[field.key] as McpProjectSettingsData[OverrideKey];
    setOverride(field.key, on ? (effective ?? seedFor(field)) : null);
  }

  function onSave() {
    for (const section of OVERRIDE_SECTIONS) {
      for (const f of section.fields) {
        if (locked.has(f.key) || !isOverridden(f.key)) continue;
        const v = draft![f.key];
        if (f.kind === 'int' || f.kind === 'float') {
          const n = v as number;
          const badInt = f.kind === 'int' && !Number.isInteger(n);
          if (
            !Number.isFinite(n) ||
            badInt ||
            (f.min !== undefined && n < f.min) ||
            (f.max !== undefined && n > f.max)
          ) {
            toast.error(
              `${f.label}: enter a valid value${f.min !== undefined ? ` ≥ ${f.min}` : ''}${f.max !== undefined ? ` ≤ ${f.max}` : ''}, or turn the override off.`,
            );
            return;
          }
        }
      }
    }
    // A locked field can never carry an override; drop any stale one before sending.
    const payload: McpProjectSettingsData = { ...draft };
    for (const name of locked) {
      delete (payload as Record<string, unknown>)[name];
    }
    updateMutation.mutate(
      { projectId, data: payload },
      {
        onSuccess: () => toast.success(`Project overrides saved for ${projectName}.`),
      },
    );
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      {header(
        'Checked fields override the global value for this project; unchecked fields inherit it (shown greyed).',
        <Button
          variant="primary"
          type="button"
          onClick={onSave}
          disabled={updateMutation.isPending}
        >
          {updateMutation.isPending ? 'Saving…' : 'Save overrides'}
        </Button>,
      )}

      {data.lockedFields.length > 0 && (
        <Banner
          tone="warn"
          icon={<Lock />}
          title="Locked by deployment configuration"
          sub={`${data.lockedFields.join(', ')} ${data.lockedFields.length === 1 ? 'is' : 'are'} pinned by Beacon:Mcp and cannot be changed here or globally.`}
        />
      )}
      {data.clampedFields.length > 0 && (
        <Banner
          tone="info"
          icon={<ShieldAlert />}
          title="Clamped to deployment ceiling"
          sub={`${data.clampedFields.join(', ')}: the effective value is capped by Beacon:Mcp:Ceilings even if a higher value is saved.`}
        />
      )}

      <Card className="p-4">
        <div className="flex flex-col gap-3">
          {OVERRIDE_SECTIONS.map(section => {
            const visible = section.fields.filter(f => !locked.has(f.key));
            if (visible.length === 0) return null;
            return (
              <div key={section.title} className="flex flex-col gap-3">
                <h3 className="m-0 text-sm font-semibold text-text">{section.title}</h3>
                {visible.map(f => (
                  <OverrideRow
                    key={f.key}
                    field={f}
                    overridden={isOverridden(f.key)}
                    value={isOverridden(f.key) ? draft[f.key] : data.effective[f.key]}
                    clamped={clamped.has(f.key)}
                    onToggle={on => toggleOverride(f, on)}
                    onChange={v => setOverride(f.key, v)}
                  />
                ))}
                <hr className="my-2 border-0 border-t border-border" />
              </div>
            );
          })}
        </div>
      </Card>
    </div>
  );
}

interface OverrideRowProps {
  field: OverrideFieldDef;
  overridden: boolean;
  value: unknown;
  clamped: boolean;
  onToggle: (on: boolean) => void;
  onChange: (value: McpProjectSettingsData[OverrideKey]) => void;
}

function OverrideRow({ field, overridden, value, clamped, onToggle, onChange }: OverrideRowProps) {
  const toggle = (
    <input
      type="checkbox"
      aria-label={`Override ${field.label}`}
      checked={overridden}
      onChange={e => onToggle(e.target.checked)}
    />
  );
  const inherit = !overridden ? (
    <span className="text-text-subtle">inherited from global</span>
  ) : undefined;
  const clampHint = clamped ? (
    <span className="text-info">clamped by deployment ceiling</span>
  ) : undefined;
  const hint =
    inherit || clampHint ? (
      <>
        {inherit}
        {inherit && clampHint ? ' · ' : ''}
        {clampHint}
      </>
    ) : undefined;

  if (field.kind === 'bool') {
    return (
      <div className="flex gap-3 items-center text-sm">
        {toggle}
        <label className="flex gap-2 items-center">
          <input
            type="checkbox"
            disabled={!overridden}
            checked={Boolean(value)}
            onChange={e => onChange(e.target.checked)}
          />
          {field.label}
        </label>
        {hint && <span className="text-xs">{hint}</span>}
      </div>
    );
  }

  if (field.kind === 'lines') {
    const lines = Array.isArray(value) ? (value as string[]).join('\n') : '';
    return (
      <div className="flex gap-3 items-start">
        <span className="pt-1">{toggle}</span>
        <Field label={field.label} hint={hint} className="flex-1">
          <Textarea
            rows={4}
            aria-label={field.label}
            disabled={!overridden}
            value={lines}
            onChange={e =>
              onChange(
                e.target.value
                  .split('\n')
                  .map(s => s.trim())
                  .filter(Boolean),
              )
            }
          />
        </Field>
      </div>
    );
  }

  const numeric = typeof value === 'number' && Number.isFinite(value) ? value : '';
  return (
    <div className="flex gap-3 items-start">
      <span className="pt-1">{toggle}</span>
      <Field label={field.label} hint={hint} className="flex-1">
        <Input
          type="number"
          aria-label={field.label}
          disabled={!overridden}
          min={field.min}
          max={field.max}
          step={field.step}
          value={numeric}
          onChange={e => onChange(e.target.value === '' ? Number.NaN : Number(e.target.value))}
        />
      </Field>
    </div>
  );
}

function seedFor(field: OverrideFieldDef): McpProjectSettingsData[OverrideKey] {
  switch (field.kind) {
    case 'bool':
      return false;
    case 'lines':
      return [];
    default:
      // Blank on purpose: OverrideRow renders NaN as an empty input and onSave refuses it with a
      // per-field toast, so the admin must type a value rather than persist the minimum (for Max
      // EXPLAIN cost that would be the harshest possible cap).
      return Number.NaN;
  }
}

function lowerFirst(name: string): string {
  return name.length === 0 ? name : name[0].toLowerCase() + name.slice(1);
}

function ProjectContextPreview() {
  const projectsQuery = useProjectsQuery();
  const [selectedProjectId, setSelectedProjectId] = useState<number | undefined>(undefined);

  const contextQuery = useQuery({
    queryKey: ['project-mcp-context', selectedProjectId],
    queryFn: async () =>
      unwrap<{ context: string }>(
        await beaconApi().getProjectMcpContext(selectedProjectId as number),
      ),
    enabled: selectedProjectId !== undefined,
  });

  const projects = projectsQuery.data?.entries ?? [];

  return (
    <div className="flex flex-col gap-3">
      <p className="text-text-muted text-sm">
        Preview the knowledge-graph context that Beacon injects into MCP tool calls for a selected
        project.
      </p>
      <Field label="Project">
        <Select
          value={selectedProjectId ?? ''}
          onChange={e => setSelectedProjectId(e.target.value ? Number(e.target.value) : undefined)}
        >
          <option value="">— Select project —</option>
          {projects.map(p => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
      </Field>

      {contextQuery.isLoading && <div className="text-text-muted">Loading context…</div>}
      {contextQuery.isError && (
        <div className="text-xs text-crit">
          Failed to load context:{' '}
          {contextQuery.error instanceof Error ? contextQuery.error.message : 'unknown error'}
        </div>
      )}
      {contextQuery.data && (
        <pre className="mono bg-surface-2 border border-border rounded-md p-4 text-xs leading-relaxed overflow-x-auto max-h-[500px] whitespace-pre-wrap break-words m-0">
          {contextQuery.data.context || '(empty context)'}
        </pre>
      )}
      {!contextQuery.isLoading &&
        !contextQuery.isError &&
        !contextQuery.data &&
        selectedProjectId && (
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
    retainQueryContent: data?.retainQueryContent ?? true,
    statementTimeoutSeconds: data?.statementTimeoutSeconds ?? 30,
    maxResultBytes: data?.maxResultBytes ?? 262144,
    maxExplainCostText:
      data?.maxExplainCost === null || data?.maxExplainCost === undefined
        ? ''
        : String(data.maxExplainCost),
    maxConcurrentQueriesPerKey: data?.maxConcurrentQueriesPerKey ?? 4,
    allowExplicitFeedbackContent: data?.allowExplicitFeedbackContent ?? true,
  };
}
