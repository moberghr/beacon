import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import {
  ArrowLeft,
  Check,
  ChevronRight,
  GitBranch,
  Info,
  Lightbulb,
  Plus,
  RefreshCw,
  X,
  Zap,
} from 'lucide-react';
import {
  PageHeader,
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Button,
  Pill,
  Field,
  Input,
  Textarea,
  Select,
  Kbd,
} from '@/components/beacon';
import { InputPromptDialog } from '@/components/ui/InputPromptDialog';
import { NewStepEditorWithExplorer } from './new/NewStepEditorWithExplorer';
import { InfoRow, CheckRow, type CheckTone } from './new/atoms';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import {
  PARAMETER_TYPE,
  PARAMETER_TYPE_LABEL,
  type ParameterTypeId,
  type UpdateQueryStepPayload,
  type UpdateQueryStepParameterPayload,
  useCreateQuery,
  useUpdateQueryMutation,
} from './queries';

interface DraftStep {
  draftId: number;
  name: string;
  sqlValue: string;
  dataSourceId: number;
  dataSourceName: string;
  dataSourceType: number;
  databaseEngineType: number | null;
  parameters: DraftParameter[];
}

interface DraftParameter {
  name: string;
  type: ParameterTypeId;
  description: string | null;
  placeholder: string | null;
}

import { detectParameters } from './helpers/parameters';

let nextDraftId = 1;
function newDraftId(): number {
  return nextDraftId++;
}

function emptyStep(num: number): DraftStep {
  return {
    draftId: newDraftId(),
    name: `Step ${num}`,
    sqlValue: '',
    dataSourceId: 0,
    dataSourceName: '',
    dataSourceType: 1,
    databaseEngineType: null,
    parameters: [],
  };
}

function toStepPayload(step: DraftStep, idx: number): UpdateQueryStepPayload {
  const parameters: UpdateQueryStepParameterPayload[] = step.parameters.map(p => ({
    name: p.name,
    type: p.type,
    description: p.description,
    placeholder: p.placeholder,
  }));
  return {
    stepId: 0,
    stepOrder: idx + 1,
    name: step.name,
    description: null,
    sqlValue: step.sqlValue,
    dataSourceId: step.dataSourceId,
    dataSourceName: step.dataSourceName,
    dataSourceType: step.dataSourceType,
    databaseEngineType: step.databaseEngineType,
    parameters,
  };
}

export default function NewQueryPage() {
  const navigate = useNavigate();
  const dataSources = useDataSourcesQuery();
  const create = useCreateQuery();
  const [createdId, setCreatedId] = useState<number | null>(null);
  const update = useUpdateQueryMutation(createdId ?? undefined);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [steps, setSteps] = useState<DraftStep[]>(() => [emptyStep(1)]);
  const [parameterPromptDraftId, setParameterPromptDraftId] = useState<number | null>(null);

  const dataSourceOptions = dataSources.data?.entries ?? [];

  useEffect(() => {
    if (dataSourceOptions.length === 0) return;
    setSteps(prev => {
      let changed = false;
      const next = prev.map(s => {
        if (s.dataSourceId !== 0) return s;
        changed = true;
        const ds = dataSourceOptions[0];
        return {
          ...s,
          dataSourceId: ds.id,
          dataSourceName: ds.name,
        };
      });
      return changed ? next : prev;
    });
  }, [dataSourceOptions]);

  const dataSourceLookup = useMemo(() => {
    const map = new Map<number, { name: string; engine: string }>();
    for (const ds of dataSourceOptions) {
      map.set(ds.id, { name: ds.name, engine: ds.databaseEngineType ?? ds.dataSourceType });
    }
    return map;
  }, [dataSourceOptions]);

  const trimmedName = name.trim();
  const trimmedDesc = description.trim();
  const totalParams = steps.reduce((acc, s) => acc + s.parameters.length, 0);
  const sourcesUsed = new Set(steps.map(s => s.dataSourceName).filter(Boolean));

  const checks: { tone: CheckTone; title: string; detail: string }[] = [
    {
      tone: trimmedName ? 'ok' : 'warn',
      title: 'Query name is required',
      detail: trimmedName ? trimmedName : 'add a descriptive name',
    },
    {
      tone: trimmedDesc ? 'ok' : 'warn',
      title: 'Description is required',
      detail: trimmedDesc ? `${trimmedDesc.length}/280 chars` : 'explain what this measures',
    },
    {
      tone: steps.every(s => s.sqlValue.trim().length > 0) ? 'ok' : 'warn',
      title: 'All steps have SQL',
      detail: steps.length === 1 ? `1 step` : `${steps.length} steps`,
    },
    {
      tone: steps.every(s => s.dataSourceId !== 0) ? 'ok' : 'warn',
      title: 'Each step has a target source',
      detail: sourcesUsed.size === 0 ? '—' : Array.from(sourcesUsed).join(', '),
    },
    {
      tone: 'pending',
      title: 'Run to validate output',
      detail: 'available after save',
    },
  ];
  const failing = checks.filter(c => c.tone === 'warn').length;

  const updateStep = (draftId: number, patch: Partial<DraftStep>) => {
    setSteps(prev => prev.map(s => (s.draftId === draftId ? { ...s, ...patch } : s)));
  };

  const onSqlChange = (draftId: number, sql: string) => {
    setSteps(prev =>
      prev.map(s =>
        s.draftId === draftId
          ? { ...s, sqlValue: sql, parameters: detectParameters(sql, s.parameters) }
          : s,
      ),
    );
  };

  const onRescan = (draftId: number) => {
    setSteps(prev =>
      prev.map(s =>
        s.draftId === draftId
          ? { ...s, parameters: detectParameters(s.sqlValue, s.parameters) }
          : s,
      ),
    );
  };

  const onAddParameter = (draftId: number) => {
    setParameterPromptDraftId(draftId);
  };

  const validateParameterName = (raw: string): string | null => {
    const cleaned = raw.trim().replace(/^\{|\}$/g, '');
    if (!cleaned) return 'Parameter name cannot be empty.';
    if (!/^\w+$/.test(cleaned)) return 'Parameter name must be alphanumeric (a-z, 0-9, _).';
    const targetStep = steps.find(s => s.draftId === parameterPromptDraftId);
    if (targetStep?.parameters.some(p => p.name === cleaned)) {
      return `Parameter "${cleaned}" already exists on this step.`;
    }
    return null;
  };

  const confirmAddParameter = (raw: string) => {
    if (parameterPromptDraftId === null) return;
    const cleaned = raw.trim().replace(/^\{|\}$/g, '');
    setSteps(prev =>
      prev.map(s => {
        if (s.draftId !== parameterPromptDraftId) return s;
        if (s.parameters.some(p => p.name === cleaned)) return s;
        return {
          ...s,
          parameters: [
            ...s.parameters,
            {
              name: cleaned,
              type: PARAMETER_TYPE.String,
              description: null,
              placeholder: `{${cleaned}}`,
            },
          ],
        };
      }),
    );
    setParameterPromptDraftId(null);
  };

  const addStep = () => {
    setSteps(prev => {
      const firstDs = dataSourceOptions[0];
      const fresh = emptyStep(prev.length + 1);
      if (firstDs) {
        fresh.dataSourceId = firstDs.id;
        fresh.dataSourceName = firstDs.name;
      }
      return [...prev, fresh];
    });
  };

  const removeStep = (draftId: number) => {
    setSteps(prev => (prev.length === 1 ? prev : prev.filter(s => s.draftId !== draftId)));
  };

  const canSave =
    trimmedName.length > 0 &&
    !create.isPending &&
    !update.isPending;

  const saveQuery = async (): Promise<number | null> => {
    if (!canSave) return null;
    try {
      // A previous attempt may have created the query but failed on the
      // follow-up step update — reuse the created id instead of duplicating.
      let newId = createdId;
      if (newId == null) {
        const created = await create.mutateAsync({
          name: trimmedName,
          description: trimmedDesc || null,
        });
        newId = created.queryId;
        setCreatedId(newId);
      }

      const hasContent = steps.some(s => s.sqlValue.trim().length > 0);
      if (hasContent) {
        await update.mutateAsync({
          queryId: newId,
          name: trimmedName,
          description: trimmedDesc || null,
          steps: steps.map(toStepPayload),
          finalQuery: null,
          finalQueryDataSourceId: null,
        });
      }
      return newId;
    } catch {
      // Toasts already raised by the mutation hooks.
      return null;
    }
  };

  const onSave = async () => {
    const newId = await saveQuery();
    if (newId == null) return;
    toast.success('Query created');
    navigate(`/queries/${newId}`);
  };

  const onSaveAndRun = async () => {
    const newId = await saveQuery();
    if (newId == null) return;
    toast.success('Query created — running…');
    navigate(`/queries/${newId}?run=1`);
  };

  const onSaveRef = useRef(onSave);
  const onSaveAndRunRef = useRef(onSaveAndRun);
  onSaveRef.current = onSave;
  onSaveAndRunRef.current = onSaveAndRun;

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        void onSaveRef.current();
      } else if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
        const target = e.target as HTMLElement | null;
        const tag = target?.tagName?.toLowerCase();
        if (tag === 'input' || tag === 'textarea' || target?.isContentEditable) return;
        e.preventDefault();
        void onSaveAndRunRef.current();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  return (
    <div className="flex flex-col gap-5 p-7" data-screen-label="02 Add New Query">
      <PageHeader
        variant="signal"
        eyebrow={
          <>
            <Link to="/queries" className="hover:text-text">Queries</Link>
            <span className="eyebrow-sep">/</span>
            <span className="normal-case tracking-normal">NEW</span>
            <span className="eyebrow-sep">·</span>
            <Pill className="mono normal-case tracking-normal">DRAFT</Pill>
          </>
        }
        prefix="Compose a"
        emphasis="cross-source"
        suffix="query."
        sub={
          <>
            Chain SQL steps across data sources, pipe results between steps using{' '}
            <span className="mono">@result1</span>, and parameterize with{' '}
            <span className="mono">{`{name}`}</span>.
          </>
        }
        actions={
          <>
            <Button icon={<ArrowLeft />} onClick={() => navigate('/queries')}>Back</Button>
            <Button
              icon={<Zap />}
              onClick={onSaveAndRun}
              disabled={!canSave}
              title="Save and run this query"
            >
              {create.isPending || update.isPending ? 'Saving…' : 'Save & run'}
            </Button>
            <Button
              variant="primary"
              icon={<Check />}
              onClick={onSave}
              disabled={!canSave}
            >
              {create.isPending || update.isPending ? 'Saving…' : 'Save query'}
            </Button>
          </>
        }
      />

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
          <Card>
            <CardHead>
              <Info className="size-3.5 text-text-muted" />
              <CardTitle>Query details</CardTitle>
              <CardActions>
                <Pill className="mono normal-case tracking-normal">UNSAVED</Pill>
              </CardActions>
            </CardHead>
            <CardBody>
              <div className="flex flex-col gap-3">
                <Field
                  label={<>Query name <span className="text-crit">*</span></>}
                  hint="A short, descriptive name. Shown in lists and notifications."
                >
                  <Input
                    id="new-query-name"
                    placeholder="e.g. EU signups vs revenue — daily"
                    value={name}
                    maxLength={200}
                    onChange={e => setName(e.target.value)}
                  />
                </Field>
                <Field
                  label={<>Description <span className="text-crit">*</span></>}
                  hint={`${description.length}/280 · plain text.`}
                >
                  <Textarea
                    id="new-query-desc"
                    placeholder="What does this query measure, and why? Include thresholds and audience."
                    value={description}
                    maxLength={1000}
                    onChange={e => setDescription(e.target.value)}
                  />
                </Field>
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHead>
              <GitBranch className="size-3.5 text-text-muted" />
              <CardTitle>Cross-data-source query builder</CardTitle>
              <CardSub>
                {steps.length} step{steps.length === 1 ? '' : 's'} · {totalParams} param{totalParams === 1 ? '' : 's'}
              </CardSub>
              <CardActions>
                <Button variant="primary" icon={<Plus />} onClick={addStep}>Add step</Button>
              </CardActions>
            </CardHead>
            <CardBody>
              <div className="flex flex-col gap-3.5">
                {steps.map((step, idx) => {
                  const ds = dataSourceLookup.get(step.dataSourceId);
                  return (
                    <div key={step.draftId} className="border border-border rounded-md overflow-hidden bg-surface">
                      <div className="flex items-center gap-2 px-3 py-2 bg-surface-2 border-b border-border">
                        <span className="mono text-2xs font-semibold px-1.5 py-0.5 rounded-xs bg-surface border border-border-strong">
                          {String(idx + 1).padStart(2, '0')}
                        </span>
                        <span className="text-sm font-medium">Step {idx + 1}</span>
                        <Pill className="mono normal-case tracking-normal">not run</Pill>
                        <div className="ml-auto flex gap-1">
                          <Button
                            size="sm"
                            variant="ghost"
                            title="Remove step"
                            onClick={() => removeStep(step.draftId)}
                            disabled={steps.length === 1}
                          >
                            <X size={14} />
                          </Button>
                        </div>
                      </div>
                      <div className="p-3 flex flex-col gap-3">
                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                          <Field label="Step name">
                            <Input
                              value={step.name}
                              onChange={e => updateStep(step.draftId, { name: e.target.value })}
                            />
                          </Field>
                          <Field label="Target database" hint={ds ? ds.engine : undefined}>
                            <Select
                              value={step.dataSourceId || ''}
                              onChange={e => {
                                const newId = Number(e.target.value);
                                const meta = dataSourceLookup.get(newId);
                                updateStep(step.draftId, {
                                  dataSourceId: newId,
                                  dataSourceName: meta?.name ?? '',
                                });
                              }}
                            >
                              <option value="">Select data source…</option>
                              {dataSourceOptions.map(d => (
                                <option key={d.id} value={d.id}>
                                  {d.name} ({d.databaseEngineType ?? d.dataSourceType})
                                </option>
                              ))}
                            </Select>
                          </Field>
                        </div>

                        <div>
                          <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">SQL query</div>
                          <div className="text-xs text-text-subtle mb-2">
                            {idx === 0 ? (
                              <>First step has no upstream results. Use <span className="mono tok-prm">{`{param_name}`}</span> for parameters.</>
                            ) : (
                              <>
                                Reference earlier steps as <span className="mono tok-ref">@result{idx}</span>
                                {idx > 1 && <> … <span className="mono tok-ref">@result1</span></>}.
                              </>
                            )}
                          </div>
                          <NewStepEditorWithExplorer
                            draftId={step.draftId}
                            dataSourceId={step.dataSourceId}
                            sqlValue={step.sqlValue}
                            onSqlChange={sql => onSqlChange(step.draftId, sql)}
                            parameterNames={step.parameters.map(p => p.name)}
                            crossStepResultCount={idx}
                          />
                        </div>

                        <div>
                          <div className="flex items-center justify-between">
                            <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted inline-flex items-center gap-2">
                              Parameters
                              <Pill className="mono normal-case tracking-normal">
                                {step.parameters.length === 0 ? 'none' : `${step.parameters.length} detected`}
                              </Pill>
                            </div>
                            <Button variant="ghost" size="sm" icon={<RefreshCw />} onClick={() => onRescan(step.draftId)}>
                              Re-scan
                            </Button>
                          </div>
                          <div className="flex flex-wrap gap-2 mt-2 items-center">
                            {step.parameters.map(p => (
                              <span
                                key={p.name}
                                className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-xs bg-surface-2 border border-border mono text-xs"
                              >
                                <span>{`{${p.name}}`}</span>
                                <select
                                  value={p.type}
                                  onChange={e => {
                                    const nextType = Number(e.target.value) as ParameterTypeId;
                                    updateStep(step.draftId, {
                                      parameters: step.parameters.map(x =>
                                        x.name === p.name ? { ...x, type: nextType } : x,
                                      ),
                                    });
                                  }}
                                  className="border-none bg-transparent font-inherit text-inherit cursor-pointer"
                                >
                                  {Object.entries(PARAMETER_TYPE_LABEL).map(([k, label]) => (
                                    <option key={k} value={k}>{label}</option>
                                  ))}
                                </select>
                              </span>
                            ))}
                            <button
                              type="button"
                              className="inline-flex items-center gap-1 px-2 py-0.5 rounded-xs border border-dashed border-border text-text-muted text-xs hover:bg-surface-2"
                              onClick={() => onAddParameter(step.draftId)}
                            >
                              <Plus size={11} /> add parameter
                            </button>
                          </div>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHead>
              <GitBranch className="size-3.5 text-text-muted" />
              <CardTitle>Query flow</CardTitle>
              <CardSub>step graph</CardSub>
            </CardHead>
            <CardBody>
              <div className="flex flex-wrap items-center gap-2">
                {steps.map((step, idx) => {
                  const ds = dataSourceLookup.get(step.dataSourceId);
                  const showResult = idx < steps.length - 1;
                  return (
                    <div key={step.draftId} className="contents">
                      <div className="px-3 py-2 rounded-md border border-border bg-surface min-w-[160px]">
                        <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
                          STEP {idx + 1} · {(step.name || `STEP ${idx + 1}`).toUpperCase()}
                        </div>
                        <div className="text-xs text-text-muted mt-0.5">
                          {ds ? `${ds.name} · ${ds.engine}` : 'no source'}
                        </div>
                      </div>
                      <ChevronRight size={14} className="text-text-subtle" />
                      {showResult && (
                        <>
                          <div className="px-3 py-2 rounded-md border border-info/30 bg-info-bg min-w-[120px]">
                            <div className="text-2xs font-semibold uppercase tracking-eyebrow text-info mono normal-case tracking-normal">@result{idx + 1}</div>
                            <div className="text-xs text-text-muted mt-0.5">intermediate result</div>
                          </div>
                          <ChevronRight size={14} className="text-text-subtle" />
                        </>
                      )}
                    </div>
                  );
                })}
                <button
                  type="button"
                  onClick={addStep}
                  className="inline-flex items-center gap-1 px-3 py-2 rounded-md border border-dashed border-border text-text-muted text-xs hover:bg-surface-2"
                >
                  <Plus size={12} /> Pipe to next step
                </button>
              </div>
            </CardBody>
          </Card>
        </div>

        <aside className="flex flex-col gap-5">
          <Card>
            <CardHead>
              <Info className="size-3.5 text-text-muted" />
              <CardTitle>Query info</CardTitle>
            </CardHead>
            <CardBody className="flex flex-col gap-2.5">
              <InfoRow label="Steps" value={String(steps.length)} />
              <InfoRow
                label="Sources used"
                value={String(sourcesUsed.size)}
                detail={Array.from(sourcesUsed).join(', ') || '—'}
              />
              <InfoRow
                label="Parameters"
                value={String(totalParams)}
                detail={
                  steps.flatMap(s => s.parameters.map(p => `{${p.name}}`)).join(', ') || '—'
                }
              />
              <InfoRow label="Estimated cost" value="—" detail="run to estimate" />
            </CardBody>
          </Card>

          <Card>
            <CardHead>
              <Check className="size-3.5 text-text-muted" />
              <CardTitle>Pre-flight checks</CardTitle>
              <CardActions>
                <Pill tone={failing > 0 ? 'warn' : 'ok'}>
                  {failing > 0 ? `${failing} to fix` : 'all green'}
                </Pill>
              </CardActions>
            </CardHead>
            <CardBody className="flex flex-col gap-2">
              {checks.map(c => (
                <CheckRow key={c.title} tone={c.tone} title={c.title} detail={c.detail} />
              ))}
            </CardBody>
          </Card>

          <div className="flex items-start gap-3 p-3.5 rounded-md border border-info/30 bg-info-bg">
            <Lightbulb className="size-4 text-info shrink-0 mt-0.5" />
            <div className="text-sm">
              <div className="font-medium">Tip · pipe results</div>
              <div className="text-xs text-text-muted mt-0.5">
                Reference a previous step using <span className="mono">@result1</span> in your{' '}
                <span className="mono">FROM</span> clause to chain across sources.
              </div>
            </div>
          </div>
        </aside>
      </div>

      <div className="flex items-center gap-2 px-5 py-3 border-t border-border bg-surface-2 rounded-md">
        <div className="text-2xs text-text-muted flex items-center gap-1.5">
          <Pill className="mono normal-case tracking-normal">DRAFT</Pill>
          <span>Click a table in the explorer to insert it at the cursor.</span>
        </div>
        <div className="ml-auto flex items-center gap-1.5">
          <span className="text-2xs text-text-muted flex items-center gap-1.5 mr-2">
            <span>Press</span>
            <Kbd>⌘</Kbd><Kbd>↵</Kbd>
            <span>to run ·</span>
            <Kbd>⌘</Kbd><Kbd>S</Kbd>
            <span>to save</span>
          </span>
          <Button
            icon={<Zap />}
            onClick={onSaveAndRun}
            disabled={!canSave}
            title="Save and run this query"
          >
            {create.isPending || update.isPending ? 'Saving…' : 'Save & run'}
          </Button>
          <Button
            variant="primary"
            icon={<Check />}
            onClick={onSave}
            disabled={!canSave}
          >
            {create.isPending || update.isPending ? 'Saving…' : 'Save query'}
          </Button>
        </div>
      </div>

      <InputPromptDialog
        open={parameterPromptDraftId !== null}
        title="Add parameter"
        message="Reference this parameter in the SQL with { } around the name — e.g. { startDate }."
        label="Parameter name"
        placeholder="startDate"
        confirmLabel="Add"
        validate={validateParameterName}
        onConfirm={confirmAddParameter}
        onCancel={() => setParameterPromptDraftId(null)}
      />
    </div>
  );
}

