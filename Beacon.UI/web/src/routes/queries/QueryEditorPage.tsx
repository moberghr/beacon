import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  GitBranch,
  Layers,
  Plus,
  Search,
  X,
  Zap,
} from 'lucide-react';
import {
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
} from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import { ParameterType } from '@/lib/enums';
import {
  PARAMETER_TYPE_LABEL,
  type ParameterValueInput,
  type QueryDetail,
  type QueryStep,
  type QueryStepPreviewResult,
  type QueryExecutionPreviewResult,
  type UpdateQueryPayload,
  type UpdateQueryStepPayload,
  useQueryDetailQuery,
  usePreviewQueryMutation,
  usePreviewStepMutation,
  useUpdateQueryMutation,
} from './queries';
import { StepParameterDialog } from './parts/StepParameterDialog';
import { PreviewResultsCard } from './parts/PreviewResultsCard';
import { StepEditorWithExplorer } from './parts/StepEditorWithExplorer';
import { detectParameters as detectParametersShared } from './helpers/parameters';

interface EditorState {
  name: string;
  description: string;
  steps: EditorStep[];
  finalQuery: string | null;
  finalQueryDataSourceId: number | null;
}

interface EditorStep {
  stepId: number;
  stepOrder: number;
  name: string;
  description: string | null;
  sqlValue: string;
  dataSourceId: number;
  dataSourceName: string;
  dataSourceType: number;
  databaseEngineType: number | null;
  parameters: EditorParameter[];
}

interface EditorParameter {
  name: string;
  type: ParameterType;
  description: string | null;
  placeholder: string | null;
}

function detectParameters(sql: string, existing: EditorParameter[]): EditorParameter[] {
  return detectParametersShared<EditorParameter>(sql, existing);
}

function fromDetail(detail: QueryDetail): EditorState {
  return {
    name: detail.name,
    description: detail.description ?? '',
    steps: [...detail.steps]
      .sort((a, b) => a.stepOrder - b.stepOrder)
      .map(stepFromWire),
    finalQuery: detail.finalQuery,
    finalQueryDataSourceId: detail.finalQueryDataSourceId,
  };
}

function stepFromWire(s: QueryStep): EditorStep {
  return {
    stepId: s.stepId,
    stepOrder: s.stepOrder,
    name: s.name,
    description: s.description,
    sqlValue: s.sqlValue,
    dataSourceId: s.dataSourceId,
    dataSourceName: s.dataSourceName,
    dataSourceType: s.dataSourceType,
    databaseEngineType: s.databaseEngineType,
    parameters: s.parameters.map(p => ({
      name: p.name,
      type: (p.type as ParameterType) ?? ParameterType.String,
      description: p.description,
      placeholder: p.placeholder,
    })),
  };
}

function toPayload(state: EditorState, queryId: number): UpdateQueryPayload {
  const steps: UpdateQueryStepPayload[] = state.steps.map((s, idx) => ({
    stepId: s.stepId,
    stepOrder: idx + 1,
    name: s.name,
    description: s.description,
    sqlValue: s.sqlValue,
    dataSourceId: s.dataSourceId,
    dataSourceName: s.dataSourceName,
    dataSourceType: s.dataSourceType,
    databaseEngineType: s.databaseEngineType,
    parameters: s.parameters.map(p => ({
      name: p.name,
      type: p.type,
      description: p.description,
      placeholder: p.placeholder,
    })),
  }));
  return {
    queryId,
    name: state.name,
    description: state.description.trim() ? state.description : null,
    steps,
    finalQuery: state.finalQuery,
    finalQueryDataSourceId: state.finalQueryDataSourceId,
  };
}

export default function QueryEditorPage() {
  const params = useParams<{ id: string }>();
  const navigate = useNavigate();
  const id = Number(params.id);
  const validId = Number.isFinite(id) ? id : undefined;

  const detail = useQueryDetailQuery(validId);
  const dataSources = useDataSourcesQuery();
  const update = useUpdateQueryMutation();
  const previewStep = usePreviewStepMutation(validId);
  const previewQuery = usePreviewQueryMutation(validId);

  const [state, setState] = useState<EditorState | null>(null);
  const [paramDialog, setParamDialog] = useState<{
    stepOrder: number;
    parameters: EditorParameter[];
  } | null>(null);
  const [stepResult, setStepResult] = useState<QueryStepPreviewResult | null>(null);
  const [queryResult, setQueryResult] = useState<QueryExecutionPreviewResult | null>(null);

  useEffect(() => {
    if (detail.data && state == null) {
      setState(fromDetail(detail.data));
    }
  }, [detail.data, state]);

  const dataSourceOptions = dataSources.data?.entries ?? [];

  const dataSourceLookup = useMemo(() => {
    const map = new Map<number, { name: string; engine: string }>();
    for (const ds of dataSourceOptions) {
      map.set(ds.id, { name: ds.name, engine: ds.databaseEngineType ?? ds.dataSourceType });
    }
    return map;
  }, [dataSourceOptions]);

  const detailSnapshot = useMemo(
    () => (detail.data ? JSON.stringify(fromDetail(detail.data)) : null),
    [detail.data],
  );

  const dirty = useMemo(
    () => state != null && detailSnapshot != null && JSON.stringify(state) !== detailSnapshot,
    [state, detailSnapshot],
  );

  if (!Number.isFinite(id)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState icon={<AlertTriangle size={20} />} title="Invalid query id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load query"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !state) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <div className="text-text-muted">Loading query…</div>
      </div>
    );
  }

  const updateStep = (stepOrder: number, patch: Partial<EditorStep>) => {
    setState(prev => {
      if (!prev) return prev;
      return {
        ...prev,
        steps: prev.steps.map(s => (s.stepOrder === stepOrder ? { ...s, ...patch } : s)),
      };
    });
  };

  const onSqlChange = (stepOrder: number, sql: string) => {
    setState(prev => {
      if (!prev) return prev;
      return {
        ...prev,
        steps: prev.steps.map(s =>
          s.stepOrder === stepOrder
            ? { ...s, sqlValue: sql, parameters: detectParameters(sql, s.parameters) }
            : s,
        ),
      };
    });
  };

  const addStep = () => {
    setState(prev => {
      if (!prev) return prev;
      const nextOrder = prev.steps.length + 1;
      const firstDs = dataSourceOptions[0];
      const newStep: EditorStep = {
        stepId: 0,
        stepOrder: nextOrder,
        name: `Step ${nextOrder}`,
        description: null,
        sqlValue: '',
        dataSourceId: firstDs?.id ?? 0,
        dataSourceName: firstDs?.name ?? '',
        dataSourceType: 1,
        databaseEngineType: null,
        parameters: [],
      };
      return { ...prev, steps: [...prev.steps, newStep] };
    });
  };

  const removeStep = (stepOrder: number) => {
    setState(prev => {
      if (!prev) return prev;
      const filtered = prev.steps.filter(s => s.stepOrder !== stepOrder);
      const renumbered = filtered.map((s, idx) => ({
        ...s,
        stepOrder: idx + 1,
      }));
      return { ...prev, steps: renumbered };
    });
  };

  const moveStep = (stepOrder: number, dir: -1 | 1) => {
    setState(prev => {
      if (!prev) return prev;
      const idx = prev.steps.findIndex(s => s.stepOrder === stepOrder);
      const next = idx + dir;
      if (idx < 0 || next < 0 || next >= prev.steps.length) return prev;
      const reordered = [...prev.steps];
      [reordered[idx], reordered[next]] = [reordered[next], reordered[idx]];
      return {
        ...prev,
        steps: reordered.map((s, i) => ({ ...s, stepOrder: i + 1 })),
      };
    });
  };

  // Save, then re-seed local state from the refetched detail so server-assigned
  // ids (new steps start as stepId 0) land in state and `dirty` resets. If the
  // user kept typing during the round-trip the state object identity changed —
  // skip the reset in that case so their keystrokes aren't discarded.
  const save = async () => {
    if (validId == null) return;
    const stateAtSave = state;
    await update.mutateAsync(toPayload(state, validId));
    const fresh = await detail.refetch();
    if (fresh.data) {
      const freshState = fromDetail(fresh.data);
      setState(prev => (prev === stateAtSave ? freshState : prev));
    }
  };

  const saveIfDirty = async () => {
    if (!dirty) return;
    await save();
  };

  const onPreviewStep = async (step: EditorStep) => {
    if (step.parameters.length > 0) {
      setParamDialog({ stepOrder: step.stepOrder, parameters: step.parameters });
      return;
    }
    try {
      await saveIfDirty();
      const result = await previewStep.mutateAsync({ stepOrder: step.stepOrder });
      setStepResult(result);
      setQueryResult(null);
    } catch {
      // Error toasts already raised by the mutation hooks.
    }
  };

  const onParamSubmit = async (values: ParameterValueInput[]) => {
    if (!paramDialog) return;
    const stepOrder = paramDialog.stepOrder;
    setParamDialog(null);
    try {
      await saveIfDirty();
      const result = await previewStep.mutateAsync({ stepOrder, parameters: values });
      setStepResult(result);
      setQueryResult(null);
    } catch {
      // Error toasts already raised by the mutation hooks.
    }
  };

  const onRunQuery = async () => {
    try {
      await saveIfDirty();
      const result = await previewQuery.mutateAsync();
      setQueryResult(result);
      setStepResult(null);
    } catch {
      // Error toasts already raised by the mutation hooks.
    }
  };

  const onSave = async () => {
    try {
      await save();
    } catch {
      // Error toast already raised by the update mutation hook.
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7" data-screen-label="03b Query Editor">
      <Card>
        <CardHead>
          <Search className="size-4 text-text-muted" />
          <CardTitle>Edit query</CardTitle>
          <CardSub>
            {state.steps.length} step{state.steps.length === 1 ? '' : 's'}
          </CardSub>
          <CardActions>
            <Button icon={<X />} onClick={() => navigate(`/queries/${id}`)}>Cancel</Button>
            <Button
              icon={<Zap />}
              onClick={onRunQuery}
              disabled={previewQuery.isPending || state.steps.length === 0}
            >
              {previewQuery.isPending ? 'Running…' : 'Run query'}
            </Button>
            <Button
              variant="primary"
              onClick={onSave}
              disabled={!dirty || update.isPending}
            >
              {update.isPending ? 'Saving…' : 'Save'}
            </Button>
          </CardActions>
        </CardHead>
        <CardBody>
          <div className="flex flex-col gap-3">
            <Field label="Name">
              <Input
                id="query-name"
                value={state.name}
                onChange={e => setState(prev => prev ? { ...prev, name: e.target.value } : prev)}
              />
            </Field>
            <Field label="Description">
              <Textarea
                id="query-description"
                rows={2}
                value={state.description}
                onChange={e =>
                  setState(prev => prev ? { ...prev, description: e.target.value } : prev)
                }
              />
            </Field>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <GitBranch className="size-3.5 text-text-muted" />
          <CardTitle>Steps</CardTitle>
          <CardActions>
            <Button icon={<Plus />} onClick={addStep}>Add step</Button>
          </CardActions>
        </CardHead>
        <CardBody>
          {state.steps.length === 0 ? (
            <span className="text-text-muted">No steps. Click “Add step”.</span>
          ) : (
            <div className="flex flex-col gap-3">
              {state.steps.map((step, idx) => {
                const ds = dataSourceLookup.get(step.dataSourceId);
                return (
                  <div key={step.stepId || `new-${idx}`} className="border border-border rounded-md overflow-hidden bg-surface">
                    <div className="flex items-center gap-2 px-3 py-2 bg-surface-2 border-b border-border">
                      <span className="mono text-2xs font-semibold px-1.5 py-0.5 rounded-xs bg-surface border border-border-strong">
                        {String(step.stepOrder).padStart(2, '0')}
                      </span>
                      <Input
                        className="flex-1"
                        value={step.name}
                        onChange={e => updateStep(step.stepOrder, { name: e.target.value })}
                      />
                      <Select
                        className="min-w-[220px]"
                        value={step.dataSourceId || ''}
                        onChange={e => {
                          const newId = Number(e.target.value);
                          const meta = dataSourceLookup.get(newId);
                          updateStep(step.stepOrder, {
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
                      <Button
                        size="sm"
                        onClick={() => moveStep(step.stepOrder, -1)}
                        disabled={idx === 0}
                        title="Move up"
                      >
                        <ArrowUp size={13} />
                      </Button>
                      <Button
                        size="sm"
                        onClick={() => moveStep(step.stepOrder, 1)}
                        disabled={idx === state.steps.length - 1}
                        title="Move down"
                      >
                        <ArrowDown size={13} />
                      </Button>
                      <Button
                        size="sm"
                        variant="danger"
                        onClick={() => removeStep(step.stepOrder)}
                        disabled={state.steps.length === 1}
                        title="Delete step"
                      >
                        <X size={13} />
                      </Button>
                    </div>
                    <div className="p-3 flex flex-col gap-3">
                      <div>
                        <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1.5">
                          SQL — use <code className="mono">{'{name}'}</code> for parameters
                          {step.stepOrder > 1 && (
                            <span className="text-text-muted ml-2 normal-case tracking-normal">
                              · refer to prior steps as <code className="mono">@result1</code>…
                              <code className="mono">{`@result${step.stepOrder - 1}`}</code>
                            </span>
                          )}
                        </div>
                        <StepEditorWithExplorer
                          editorId={`step-${step.stepOrder}-sql`}
                          dataSourceId={step.dataSourceId}
                          sqlValue={step.sqlValue}
                          onSqlChange={sql => onSqlChange(step.stepOrder, sql)}
                          parameterNames={step.parameters.map(p => p.name)}
                          crossStepResultCount={Math.max(0, step.stepOrder - 1)}
                        />
                      </div>
                      <div>
                        <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted inline-flex items-center gap-2">
                          Parameters
                          <Pill className="mono normal-case tracking-normal">
                            {step.parameters.length === 0 ? 'none' : `${step.parameters.length} detected`}
                          </Pill>
                        </div>
                        {step.parameters.length > 0 && (
                          <div className="grid mt-2 gap-2 grid-cols-[repeat(auto-fill,minmax(220px,1fr))]">
                            {step.parameters.map(p => (
                              <div
                                key={p.name}
                                className="border border-border rounded-sm p-2"
                              >
                                <div className="mono font-semibold text-xs">
                                  {`{${p.name}}`}
                                </div>
                                <Select
                                  className="mt-1.5 text-xs"
                                  value={p.type}
                                  onChange={e => {
                                    const nextType = Number(e.target.value) as ParameterType;
                                    updateStep(step.stepOrder, {
                                      parameters: step.parameters.map(x =>
                                        x.name === p.name ? { ...x, type: nextType } : x,
                                      ),
                                    });
                                  }}
                                >
                                  {Object.entries(PARAMETER_TYPE_LABEL).map(([k, label]) => (
                                    <option key={k} value={k}>
                                      {label}
                                    </option>
                                  ))}
                                </Select>
                              </div>
                            ))}
                          </div>
                        )}
                      </div>
                      <div className="flex gap-2 items-center">
                        <Button
                          icon={<Zap />}
                          onClick={() => onPreviewStep(step)}
                          disabled={previewStep.isPending}
                        >
                          {previewStep.isPending ? 'Running…' : 'Preview step'}
                        </Button>
                        <span className="text-text-muted text-xs">
                          {ds ? `${ds.name} · ${ds.engine}` : ''}
                        </span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </CardBody>
      </Card>

      {state.finalQuery && (
        <Card>
          <CardHead>
            <Layers className="size-3.5 text-text-muted" />
            <CardTitle>Final query</CardTitle>
            <CardSub>read-only</CardSub>
          </CardHead>
          <CardBody flush>
            <pre className="m-0 p-3 bg-surface-2 mono text-xs overflow-auto whitespace-pre-wrap max-h-60">
              {state.finalQuery}
            </pre>
          </CardBody>
        </Card>
      )}

      {stepResult && (
        <PreviewResultsCard
          title={`Step ${stepResult.stepOrder} preview · ${stepResult.stepName}`}
          rows={stepResult.previewResults ?? []}
          totalRows={stepResult.totalRows}
          executionTimeMs={stepResult.executionTimeMs}
          error={stepResult.success ? null : stepResult.errorMessage ?? 'Preview failed'}
          onClose={() => setStepResult(null)}
        />
      )}

      {queryResult && (
        <PreviewResultsCard
          title="Query preview"
          rows={queryResult.finalResult?.rows ?? []}
          totalRows={queryResult.finalResult?.rowCount ?? 0}
          executionTimeMs={queryResult.totalExecutionTimeMs}
          error={
            queryResult.success
              ? queryResult.finalResult?.success
                ? null
                : queryResult.finalResult?.error ?? null
              : queryResult.errorMessage ?? 'Query preview failed'
          }
          onClose={() => setQueryResult(null)}
        />
      )}

      <StepParameterDialog
        open={paramDialog != null}
        stepOrder={paramDialog?.stepOrder ?? null}
        parameters={paramDialog?.parameters ?? []}
        onClose={() => setParamDialog(null)}
        onSubmit={onParamSubmit}
      />
    </div>
  );
}
