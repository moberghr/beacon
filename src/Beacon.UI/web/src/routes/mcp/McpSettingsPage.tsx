import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useForm, type FieldErrors } from 'react-hook-form';
import { useQuery } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Eye, Lock, ShieldAlert } from 'lucide-react';
import {
  PageHeader,
  Banner,
  Button,
  Card,
  Field,
  Input,
  Textarea,
} from '@/components/beacon';
import { Tabs } from '@/components/Tabs';
import { useAccess } from '@/auth/useAuth';
import { useRequirePermission } from '@/auth/useRequirePermission';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { ProjectPicker } from '@/routes/projects/ProjectPicker';
import {
  useMcpDeploymentLocks,
  useMcpProjectSettings,
  useMcpSettings,
  useMcpTools,
  useUpdateMcpProjectSettings,
  useUpdateMcpSettings,
  type GetMcpSettingsResult,
  type McpDeploymentLock,
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
  enableSampleValueCollection: z.boolean(),
  enableValueGrounding: z.boolean(),
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

/** The settings that send values read from the data to the LLM provider, which Beacon:Mcp:ForbidRowDataToLlm pins off. */
const ROW_DATA_KEYS = ['enableSampleValueCollection', 'enableValueGrounding'] as const;

/** One label per row-data switch, on the global form and the per-project overrides alike. */
const ROW_DATA_LABEL = {
  enableSampleValueCollection: 'Collect sample values',
  enableValueGrounding: 'Ground questions in live values (value grounding)',
} as const;

/** The built-in tools whose description the global settings override, in the order the form lists them. */
const TOOL_DESCRIPTION_FIELDS = [
  { tool: 'get_context', name: 'getContextDescription' },
  { tool: 'ask', name: 'askDescription' },
  { tool: 'query', name: 'queryDescription' },
  { tool: 'get_documentation', name: 'getDocumentationDescription' },
  { tool: 'search', name: 'searchDescription' },
] as const;

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
  enableSampleValueCollection: 'guardrails',
  enableValueGrounding: 'guardrails',
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

/** Read by an Auditor or a SecurityAdmin; changed by a SecurityAdmin (an Admin holds both). */
export default function McpSettingsPage() {
  const allowed = useRequirePermission('Auditor', 'SecurityAdmin');

  if (allowed === undefined) {
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
  if (allowed === false) return null;

  return <McpSettingsShell />;
}

/** Shown to an Auditor: the settings are theirs to read, a SecurityAdmin's to change. */
function ReadOnlyBanner({ gateway = false }: { gateway?: boolean }) {
  return (
    <Banner
      tone="info"
      icon={<Eye />}
      title="Read only"
      sub={gateway
        ? 'The MCP gateway does not serve changes to the global defaults. Pick a project to change its overrides.'
        : 'You can read these settings. Changing them needs the SecurityAdmin permission.'}
    />
  );
}

/**
 * Owns the scope selector. "Global defaults" edits the single global row; a project
 * edits that project's overrides (null = inherit). The selector rides in each form's
 * header so it stays reachable while a scope is loading or failed to load.
 */
function McpSettingsShell() {
  const access = useAccess();
  const readOnly = !access.hasPermission('SecurityAdmin');
  const [scope, setScope] = useState<Scope>('global');
  // The picked project's name, for the heading; the picker fetches matches as the user types.
  const [scopeName, setScopeName] = useState<string | null>(null);

  // Picking a project lists the projects, which needs a Viewer or Editor role: without one, only the global scope.
  const scopeSelector = access.isFunctionalOnly ? null : (
    <ProjectPicker
      ariaLabel="Settings scope"
      className="w-auto min-w-[14rem]"
      value={scope === 'global' ? null : scope}
      clearLabel="Global defaults"
      onSelect={x => {
        setScope(x ? x.id : 'global');
        setScopeName(x?.name ?? null);
      }}
    />
  );

  if (scope === 'global') {
    return <McpSettingsForm scopeSelector={scopeSelector} readOnly={readOnly || access.gatewayMode} gateway={access.gatewayMode} />;
  }

  const projectName = scopeName ?? `#${scope}`;
  return (
    <ProjectSettingsForm
      key={scope}
      projectId={scope}
      projectName={projectName}
      scopeSelector={scopeSelector}
      readOnly={readOnly}
    />
  );
}

function McpSettingsForm({ scopeSelector, readOnly, gateway }: { scopeSelector: ReactNode; readOnly: boolean; gateway: boolean }) {
  const isAdmin = useAccess().isAdmin;
  const { data: loaded, isLoading, isError } = useMcpSettings();
  // The form edits the stored values, so a value above a deployment ceiling is kept by a save.
  const data = useMemo(() => editableSettings(loaded), [loaded]);
  const clamped = (loaded?.clampedFields ?? []).map(lowerFirst) as (keyof McpSettingsData)[];
  // Settings the deployment pins: their switches stay as pinned, for every role.
  const deploymentLocks = useMcpDeploymentLocks().data;
  const locked = new Set((deploymentLocks?.lockedFields ?? []).map(lowerFirst));
  const contentLock = contentLockOf(deploymentLocks?.locks);
  const safetyLocked = locked.has('enforceReadOnly') || locked.has('enablePiiDetection');
  const rowDataLocked = ROW_DATA_KEYS.some(x => locked.has(x));
  const updateMutation = useUpdateMcpSettings();
  const [tab, setTab] = useState<TabKey>('prompt');
  // Only the built-in tools the server serves are described (the MCP gateway serves no SQL tools); while the catalogue
  // is unknown (loading, or not readable by this user), every one.
  const servedTools = useMcpTools().data?.toolNames;
  const serves = (tool: string) => servedTools?.includes(tool) ?? true;

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: settingsToForm(data),
  });

  useEffect(() => {
    if (data) form.reset(settingsToForm(data));
  }, [data, form]);

  const { errors } = form.formState;

  function onSubmit(values: FormValues) {
    if (readOnly) return;
    // Blank/whitespace-only nullable text fields must round-trip back to null so
    // the backend's `?? default` fallback keeps working — saving '' would wipe
    // the default Ask system prompt and tool descriptions.
    const orNull = (s: string | null) => (s && s.trim() !== '' ? s : null);
    // Every setting the page loaded goes back as it was read, so one the page does not show is never reset to a
    // default by a save; the form's own fields overwrite theirs.
    const payload: McpSettingsData = {
      ...data,
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
      enableSampleValueCollection: values.enableSampleValueCollection,
      enableValueGrounding: values.enableValueGrounding,
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
              {!readOnly && (
                <Button variant="primary" type="submit" disabled={updateMutation.isPending}>
                  {updateMutation.isPending ? 'Saving…' : 'Save settings'}
                </Button>
              )}
            </div>
          }
        />

        {readOnly && <ReadOnlyBanner gateway={gateway} />}

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
          {/* The context preview reads a project's context and stays usable; the settings tabs are read-only for an Auditor. */}
          <fieldset disabled={readOnly} className="m-0 p-0 border-0 min-w-0">
          {tab === 'prompt' && (
            <div className="flex flex-col gap-3">
              <p className="text-text-muted text-sm">
                {serves('ask') && 'The Ask system prompt controls how the AI generates SQL from natural language. '}
                The global instruction is prepended to every tool's user context.
              </p>
              {serves('ask') && (
                <Field label="Ask tool system prompt">
                  <Textarea
                    rows={8}
                    {...form.register('askSystemPrompt')}
                    placeholder="Leave blank to use the default system prompt."
                  />
                </Field>
              )}
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
              {TOOL_DESCRIPTION_FIELDS
                .filter(x => serves(x.tool))
                .map(x => <ToolField key={x.name} label={x.tool} name={x.name} form={form} />)}
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
                <input
                  type="checkbox"
                  disabled={locked.has('enforceReadOnly') || (!isAdmin && data?.enforceReadOnly === true)}
                  {...form.register('enforceReadOnly')}
                />
                Enforce read-only queries
                {locked.has('enforceReadOnly') && <Lock className="h-3.5 w-3.5 text-text-muted" aria-label="Locked by deployment configuration" />}
              </label>
              <label className="flex gap-2 items-center text-sm">
                <input
                  type="checkbox"
                  disabled={locked.has('enablePiiDetection') || (!isAdmin && data?.enablePiiDetection === true)}
                  {...form.register('enablePiiDetection')}
                />
                Enable PII detection
                {locked.has('enablePiiDetection') && <Lock className="h-3.5 w-3.5 text-text-muted" aria-label="Locked by deployment configuration" />}
              </label>
              {safetyLocked && (
                <p className="m-0 text-xs text-text-muted">
                  Locked on by the deployment (<span className="mono">Beacon:Mcp:ForceReadOnly</span>,{' '}
                  <span className="mono">Beacon:Mcp:ForcePiiDetection</span>): nobody can turn{' '}
                  {locked.has('enforceReadOnly') && locked.has('enablePiiDetection')
                    ? 'them'
                    : locked.has('enforceReadOnly') ? 'read-only enforcement' : 'PII detection'}{' '}
                  off here or per project, an Admin included; only the host&apos;s configuration can.
                </p>
              )}
              {!readOnly && !isAdmin && !(locked.has('enforceReadOnly') && locked.has('enablePiiDetection')) && (
                <p className="text-xs text-text-muted">Only an Admin can turn read-only enforcement or PII detection off.</p>
              )}
              <Field label="Custom PII patterns (one per line)">
                <Textarea
                  rows={6}
                  {...form.register('customPiiPatternsText')}
                  placeholder={'customer_name\naccount_number\n\\biban\\b'}
                />
              </Field>

              <hr className="my-2 border-0 border-t border-border" />
              <h3 className="m-0 text-sm font-semibold text-text">Row data sent to the LLM provider</h3>
              <p className="m-0 text-text-muted text-xs">
                Off by default. While on, values read from the data sources reach the LLM provider: sample values in
                grounding context and documentation, and live values matched to the words of a question. A project can
                override either for itself.
              </p>
              <label className="flex gap-2 items-center text-sm">
                <input
                  type="checkbox"
                  disabled={locked.has('enableSampleValueCollection')}
                  {...form.register('enableSampleValueCollection')}
                />
                {ROW_DATA_LABEL.enableSampleValueCollection}
                {locked.has('enableSampleValueCollection') && <Lock className="h-3.5 w-3.5 text-text-muted" aria-label="Locked by deployment configuration" />}
              </label>
              <label className="flex gap-2 items-center text-sm">
                <input
                  type="checkbox"
                  disabled={locked.has('enableValueGrounding')}
                  {...form.register('enableValueGrounding')}
                />
                {ROW_DATA_LABEL.enableValueGrounding}
                {locked.has('enableValueGrounding') && <Lock className="h-3.5 w-3.5 text-text-muted" aria-label="Locked by deployment configuration" />}
              </label>
              {rowDataLocked && (
                <p className="m-0 text-xs text-text-muted">
                  Off by the deployment (<span className="mono">Beacon:Mcp:ForbidRowDataToLlm</span>): no value read
                  from the data is sent to the LLM provider. Nobody can turn these on here or per project, an Admin
                  included; only the host&apos;s configuration can.
                </p>
              )}

              <hr className="my-2 border-0 border-t border-border" />
              <h3 className="m-0 text-sm font-semibold text-text">Execution</h3>
              <p className="m-0 text-text-muted text-xs">
                Deployment-level locks and ceilings (<span className="mono">Beacon:Mcp</span>) win
                over these values; a save that contradicts a lock is refused.
              </p>
              {clamped.length > 0 && (
                <p className="m-0 text-text-muted text-xs">
                  Saved above a deployment ceiling, which applies instead:{' '}
                  {clamped.map(x => `${x} (${String(loaded?.[x] ?? 'no limit')})`).join(', ')}.
                </p>
              )}
              <label className="flex gap-2 items-center text-sm">
                <input
                  type="checkbox"
                  disabled={locked.has('retainQueryContent')}
                  {...form.register('retainQueryContent')}
                />
                Retain question and SQL text in signals and audit
                {locked.has('retainQueryContent') && <Lock className="h-3.5 w-3.5 text-text-muted" aria-label="Locked by deployment configuration" />}
              </label>
              {locked.has('retainQueryContent') && <ContentLockNote lock={contentLock} />}
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
                  ) : (
                    'Applies to PostgreSQL data sources: a query whose planner-estimated total cost exceeds it is refused before it runs. Other engines ignore it.'
                  )
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
          </fieldset>

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
        label: ROW_DATA_LABEL.enableSampleValueCollection,
        kind: 'bool',
      },
      {
        key: 'enableValueGrounding',
        label: ROW_DATA_LABEL.enableValueGrounding,
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

/** The safety switches only an Admin loosens, per project as globally; anyone else who may edit only tightens them. */
const SAFETY_KEYS: ReadonlySet<OverrideKey> = new Set<OverrideKey>(['enforceReadOnly', 'enablePiiDetection']);

interface ProjectSettingsFormProps {
  projectId: number;
  projectName: string;
  scopeSelector: ReactNode;
  readOnly: boolean;
}

function ProjectSettingsForm({ projectId, projectName, scopeSelector, readOnly }: ProjectSettingsFormProps) {
  const isAdmin = useAccess().isAdmin;
  const { data, isLoading, isError } = useMcpProjectSettings(projectId);
  const contentLock = contentLockOf(useMcpDeploymentLocks().data?.locks);
  // The global values decide whether clearing an override that pins a safety switch on loosens it.
  const global = useMcpSettings().data;
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

  // A non-Admin only tightens a safety switch (the server refuses the rest, 403): it keeps the switch on unless the
  // stored override already pins it off, and clears an override pinning it on only while the global value is on too.
  function mayTurnOff(key: OverrideKey): boolean {
    return isAdmin || !SAFETY_KEYS.has(key) || data!.overrides[key] === false;
  }

  function mayClear(key: OverrideKey): boolean {
    return isAdmin || !SAFETY_KEYS.has(key) || data!.overrides[key] !== true || global?.[key] === true;
  }

  function toggleOverride(field: OverrideFieldDef, on: boolean) {
    // Turning an override on seeds it with the effective value so the row shows what
    // the project will keep getting until the admin changes it. A null effective value
    // (e.g. no EXPLAIN cost limit) would read as "inherit" again, so seed a kind-appropriate
    // placeholder the admin then edits. A safety switch the caller may not turn off is pinned on.
    const effective = data!.effective[field.key] as McpProjectSettingsData[OverrideKey];
    const seed = mayTurnOff(field.key) ? (effective ?? seedFor(field)) : true;
    setOverride(field.key, on ? seed : null);
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
        readOnly ? undefined : (
          <Button
            variant="primary"
            type="button"
            onClick={onSave}
            disabled={updateMutation.isPending}
          >
            {updateMutation.isPending ? 'Saving…' : 'Save overrides'}
          </Button>
        ),
      )}

      {readOnly && <ReadOnlyBanner />}

      {data.lockedFields.length > 0 && (
        <Banner
          tone="warn"
          icon={<Lock />}
          title="Locked by deployment configuration"
          sub={`${data.lockedFields.join(', ')} ${data.lockedFields.length === 1 ? 'is' : 'are'} pinned by Beacon:Mcp and cannot be changed here or globally.${
            ROW_DATA_KEYS.some(x => locked.has(x))
              ? ' Beacon:Mcp:ForbidRowDataToLlm keeps every value read from the data away from the LLM provider.'
              : ''
          }${
            locked.has('retainQueryContent') && contentLock === REQUIRE_CONTENT_LOCK
              ? ' Beacon:Mcp:Audit:RequireContent keeps the question and SQL text of every call in the audit.'
              : ''
          }`}
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
        {!readOnly && !isAdmin && (
          <p className="m-0 mb-3 text-xs text-text-muted">
            Only an Admin can turn read-only enforcement or PII detection off for a project.
          </p>
        )}
        <fieldset disabled={readOnly} className="m-0 p-0 border-0 min-w-0 flex flex-col gap-3">
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
                    mayTurnOff={mayTurnOff(f.key)}
                    mayClear={mayClear(f.key)}
                    onToggle={on => toggleOverride(f, on)}
                    onChange={v => setOverride(f.key, v)}
                  />
                ))}
                <hr className="my-2 border-0 border-t border-border" />
              </div>
            );
          })}
        </fieldset>
      </Card>
    </div>
  );
}

interface OverrideRowProps {
  field: OverrideFieldDef;
  overridden: boolean;
  value: unknown;
  clamped: boolean;
  /** False when the caller may not turn this switch off (it stays on). */
  mayTurnOff: boolean;
  /** False when the caller may not clear this override (inheriting would loosen it). */
  mayClear: boolean;
  onToggle: (on: boolean) => void;
  onChange: (value: McpProjectSettingsData[OverrideKey]) => void;
}

function OverrideRow({ field, overridden, value, clamped, mayTurnOff, mayClear, onToggle, onChange }: OverrideRowProps) {
  const toggle = (
    <input
      type="checkbox"
      aria-label={`Override ${field.label}`}
      disabled={overridden && !mayClear}
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
            disabled={!overridden || (Boolean(value) && !mayTurnOff)}
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

/** The lock `Beacon:Mcp:Audit:RequireContent` goes by: it pins content retention on, so the audit keeps the text. */
const REQUIRE_CONTENT_LOCK = 'Audit:RequireContent';
const FORCE_NO_CONTENT_LOCK = 'ForceNoContentRetention';

/** The lock that pins content retention, when one does. */
function contentLockOf(locks: McpDeploymentLock[] | undefined): string | undefined {
  return locks?.find(x => x.fieldName === 'RetainQueryContent')?.lockName;
}

/** Why the content retention switch cannot be changed: which lock pins it, which way, and where it is unlocked. */
function ContentLockNote({ lock }: { lock: string | undefined }) {
  if (lock === REQUIRE_CONTENT_LOCK) {
    return (
      <p className="m-0 text-xs text-text-muted">
        Kept on by the deployment (<span className="mono">Beacon:Mcp:Audit:RequireContent</span>): the audit keeps the
        question and SQL text of every call, so it cannot be switched off unseen. Nobody can turn it off here or per
        project, an Admin included; only the host&apos;s configuration can.
      </p>
    );
  }

  if (lock === FORCE_NO_CONTENT_LOCK) {
    return (
      <p className="m-0 text-xs text-text-muted">
        Off by the deployment (<span className="mono">Beacon:Mcp:ForceNoContentRetention</span>): signals and the audit
        keep the structure of each call and keyed hashes of its text, never the text. Nobody can turn it on here or per
        project, an Admin included; only the host&apos;s configuration can.
      </p>
    );
  }

  return (
    <p className="m-0 text-xs text-text-muted">
      Pinned by the deployment (<span className="mono">Beacon:Mcp</span>): nobody can change it here or per project, an
      Admin included; only the host&apos;s configuration can.
    </p>
  );
}

function ProjectContextPreview() {
  const [selectedProjectId, setSelectedProjectId] = useState<number | undefined>(undefined);

  const contextQuery = useQuery({
    queryKey: ['project-mcp-context', selectedProjectId],
    queryFn: async () =>
      unwrap<{ context: string }>(
        await beaconApi().getProjectMcpContext(selectedProjectId as number),
      ),
    enabled: selectedProjectId !== undefined,
  });

  return (
    <div className="flex flex-col gap-3">
      <p className="text-text-muted text-sm">
        Preview the knowledge-graph context that Beacon injects into MCP tool calls for a selected
        project.
      </p>
      <Field label="Project">
        <ProjectPicker value={selectedProjectId} onSelect={x => setSelectedProjectId(x?.id)} />
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

/**
 * What the global form edits: the stored settings, with each field a deployment lock pins as the lock pins it (the page
 * cannot change it, and a save that contradicts the lock is refused; the server keeps the stored value).
 */
function editableSettings(loaded: GetMcpSettingsResult | undefined): McpSettingsData | undefined {
  if (!loaded) return undefined;
  const { stored, lockedFields } = loaded;
  if (!stored) return loaded;
  const editable: Record<string, unknown> = { ...stored };
  for (const field of lockedFields ?? []) {
    const key = lowerFirst(field);
    editable[key] = (loaded as unknown as Record<string, unknown>)[key];
  }
  return editable as unknown as McpSettingsData;
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
    // Off by default: both send values read from the data to the LLM provider.
    enableSampleValueCollection: data?.enableSampleValueCollection ?? false,
    enableValueGrounding: data?.enableValueGrounding ?? false,
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
