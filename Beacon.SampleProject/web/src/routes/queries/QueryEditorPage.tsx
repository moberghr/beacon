import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { SqlEditor } from '@/components/ui/SqlEditor';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import {
  PARAMETER_TYPE,
  PARAMETER_TYPE_LABEL,
  type ParameterTypeId,
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

/**
 * React port of the Blazor `QueryStepBuilder` host. Phase 3 Batch 5f.
 *
 * Scope shipped:
 *  - Multi-step editor: name, dataSource select, Monaco SQL, parameter chips
 *  - Auto-detect `{name}` parameters on edit
 *  - Reorder via up/down buttons (drag-reorder deferred)
 *  - Per-step preview (with parameter dialog when params present)
 *  - Whole-query preview
 *  - Save (PUT /beacon/api/queries/{id})
 *
 * Deferred (documented in ReactMigration-Phase3-Batch5f-Diff.md):
 *  - DatabaseExplorer side panel + table/column drag-insert
 *  - SQL completion provider driven by metadata
 *  - QueryFlowDiagram visualization
 *  - Final query stage editing (read-only display retained)
 *  - Engine-specific Api / CloudWatch editors (legacy link kept)
 */

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
  type: ParameterTypeId;
  description: string | null;
  placeholder: string | null;
}

const PARAM_REGEX = /\{(\w+)\}/g;

function detectParameters(sql: string, existing: EditorParameter[]): EditorParameter[] {
  const seen = new Set<string>();
  const detected: string[] = [];
  let match: RegExpExecArray | null;
  PARAM_REGEX.lastIndex = 0;
  while ((match = PARAM_REGEX.exec(sql)) != null) {
    const name = match[1];
    if (!seen.has(name)) {
      seen.add(name);
      detected.push(name);
    }
  }

  const byName = new Map(existing.map(p => [p.name, p]));
  return detected.map(name =>
    byName.get(name) ?? {
      name,
      type: PARAMETER_TYPE.String,
      description: null,
      placeholder: `{${name}}`,
    },
  );
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
      type: (p.type as ParameterTypeId) ?? PARAMETER_TYPE.String,
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
  const update = useUpdateQueryMutation(validId);
  const previewStep = usePreviewStepMutation(validId);
  const previewQuery = usePreviewQueryMutation(validId);

  const [state, setState] = useState<EditorState | null>(null);
  const [paramDialog, setParamDialog] = useState<{
    stepOrder: number;
    parameters: EditorParameter[];
  } | null>(null);
  const [stepResult, setStepResult] = useState<QueryStepPreviewResult | null>(null);
  const [queryResult, setQueryResult] = useState<QueryExecutionPreviewResult | null>(null);

  // Initialize editor state from loaded detail. Only re-run when the
  // backing detail object changes; the user's edits are preserved across
  // re-renders triggered by other queries (e.g. data sources).
  useEffect(() => {
    if (detail.data && state == null) {
      setState(fromDetail(detail.data));
    }
  }, [detail.data, state]);

  const dataSourceOptions = dataSources.data?.entries ?? [];

  // The data source list endpoint returns the engine name as a string,
  // but the wire shape that QueryStep uses encodes it as a numeric enum.
  // We don't need to look it up for save (we keep whatever was on the
  // step), but we need the display label for the select dropdown.
  const dataSourceLookup = useMemo(() => {
    const map = new Map<number, { name: string; engine: string }>();
    for (const ds of dataSourceOptions) {
      map.set(ds.id, { name: ds.name, engine: ds.databaseEngineType ?? ds.dataSourceType });
    }
    return map;
  }, [dataSourceOptions]);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid query id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="page">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load query"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !state) {
    return (
      <div className="page">
        <div className="muted">Loading query…</div>
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

  const onPreviewStep = async (step: EditorStep) => {
    if (step.parameters.length > 0) {
      setParamDialog({ stepOrder: step.stepOrder, parameters: step.parameters });
      return;
    }
    const result = await previewStep.mutateAsync({ stepOrder: step.stepOrder });
    setStepResult(result);
    setQueryResult(null);
  };

  const onParamSubmit = async (values: ParameterValueInput[]) => {
    if (!paramDialog) return;
    const stepOrder = paramDialog.stepOrder;
    setParamDialog(null);
    const result = await previewStep.mutateAsync({ stepOrder, parameters: values });
    setStepResult(result);
    setQueryResult(null);
  };

  const onRunQuery = async () => {
    const result = await previewQuery.mutateAsync();
    setQueryResult(result);
    setStepResult(null);
  };

  const onSave = async () => {
    if (validId == null) return;
    await update.mutateAsync(toPayload(state, validId));
  };

  const dirty = detail.data ? JSON.stringify(state) !== JSON.stringify(fromDetail(detail.data)) : false;

  return (
    <div className="page" data-screen-label="03b Query Editor">
      <div className="card">
        <div className="card__head">
          <Icon.Query size={16} className="muted" />
          <h2 className="card__title">Edit query</h2>
          <span className="card__sub">
            {state.steps.length} step{state.steps.length === 1 ? '' : 's'}
          </span>
          <div className="card__actions" style={{ display: 'flex', gap: 8 }}>
            <button className="btn" type="button" onClick={() => navigate(`/queries/${id}`)}>
              <Icon.X size={13} className="btn__icon" /> Cancel
            </button>
            <button
              className="btn"
              type="button"
              onClick={onRunQuery}
              disabled={previewQuery.isPending || state.steps.length === 0}
            >
              <Icon.Bolt size={13} className="btn__icon" />
              {previewQuery.isPending ? 'Running…' : 'Run query'}
            </button>
            <button
              className="btn btn--primary"
              type="button"
              onClick={onSave}
              disabled={!dirty || update.isPending}
            >
              {update.isPending ? 'Saving…' : 'Save'}
            </button>
          </div>
        </div>
        <div className="card__body">
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div className="q-field">
              <label className="q-label" htmlFor="query-name">Name</label>
              <input
                id="query-name"
                className="q-input"
                value={state.name}
                onChange={e => setState(prev => prev ? { ...prev, name: e.target.value } : prev)}
              />
            </div>
            <div className="q-field">
              <label className="q-label" htmlFor="query-description">Description</label>
              <textarea
                id="query-description"
                className="q-textarea"
                rows={2}
                value={state.description}
                onChange={e =>
                  setState(prev => prev ? { ...prev, description: e.target.value } : prev)
                }
              />
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Branch size={15} className="muted" />
          <h3 className="card__title">Steps</h3>
          <div className="card__actions">
            <button className="btn" type="button" onClick={addStep}>
              <Icon.Plus size={13} className="btn__icon" /> Add step
            </button>
          </div>
        </div>
        <div className="card__body">
          {state.steps.length === 0 ? (
            <span className="muted">No steps. Click “Add step”.</span>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {state.steps.map((step, idx) => {
                const ds = dataSourceLookup.get(step.dataSourceId);
                return (
                  <div key={step.stepId || `new-${idx}`} className="step-card">
                    <div className="step-card__head">
                      <span className="step-num">{String(step.stepOrder).padStart(2, '0')}</span>
                      <input
                        className="q-input"
                        style={{ flex: 1 }}
                        value={step.name}
                        onChange={e => updateStep(step.stepOrder, { name: e.target.value })}
                      />
                      <select
                        className="q-input"
                        style={{ minWidth: 220 }}
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
                      </select>
                      <button
                        className="btn"
                        type="button"
                        onClick={() => moveStep(step.stepOrder, -1)}
                        disabled={idx === 0}
                        title="Move up"
                      >
                        <Icon.ArrowUp size={13} />
                      </button>
                      <button
                        className="btn"
                        type="button"
                        onClick={() => moveStep(step.stepOrder, 1)}
                        disabled={idx === state.steps.length - 1}
                        title="Move down"
                      >
                        <Icon.ArrowDown size={13} />
                      </button>
                      <button
                        className="btn btn--danger"
                        type="button"
                        onClick={() => removeStep(step.stepOrder)}
                        disabled={state.steps.length === 1}
                        title="Delete step"
                      >
                        <Icon.X size={13} />
                      </button>
                    </div>
                    <div className="step-card__body">
                      <div>
                        <div className="q-label" style={{ marginBottom: 6 }}>
                          SQL — use <code>{'{name}'}</code> for parameters
                          {step.stepOrder > 1 && (
                            <span className="muted" style={{ marginLeft: 8 }}>
                              · refer to prior steps as <code>@result1</code>…
                              <code>{`@result${step.stepOrder - 1}`}</code>
                            </span>
                          )}
                        </div>
                        <SqlEditor
                          id={`step-${step.stepOrder}-sql`}
                          height={320}
                          value={step.sqlValue}
                          onChange={sql => onSqlChange(step.stepOrder, sql)}
                        />
                      </div>
                      <div className="params" style={{ marginTop: 12 }}>
                        <div className="params__head">
                          <div className="q-label">
                            Parameters
                            <span className="pill pill--neutral mono" style={{ marginLeft: 8, fontSize: 10 }}>
                              {step.parameters.length === 0
                                ? 'none'
                                : `${step.parameters.length} detected`}
                            </span>
                          </div>
                        </div>
                        {step.parameters.length > 0 && (
                          <div
                            style={{
                              display: 'grid',
                              gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
                              gap: 8,
                              marginTop: 8,
                            }}
                          >
                            {step.parameters.map(p => (
                              <div
                                key={p.name}
                                className="param-card"
                                style={{
                                  border: '1px solid var(--border)',
                                  borderRadius: 6,
                                  padding: 8,
                                }}
                              >
                                <div className="mono" style={{ fontWeight: 600, fontSize: 12 }}>
                                  {`{${p.name}}`}
                                </div>
                                <select
                                  className="q-input"
                                  style={{ marginTop: 6, fontSize: 12 }}
                                  value={p.type}
                                  onChange={e => {
                                    const nextType = Number(e.target.value) as ParameterTypeId;
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
                                </select>
                              </div>
                            ))}
                          </div>
                        )}
                      </div>
                      <div style={{ marginTop: 12, display: 'flex', gap: 8 }}>
                        <button
                          className="btn"
                          type="button"
                          onClick={() => onPreviewStep(step)}
                          disabled={previewStep.isPending}
                        >
                          <Icon.Bolt size={13} className="btn__icon" />
                          {previewStep.isPending ? 'Running…' : 'Preview step'}
                        </button>
                        <span className="muted" style={{ fontSize: 12, alignSelf: 'center' }}>
                          {ds ? `${ds.name} · ${ds.engine}` : ''}
                        </span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>

      {state.finalQuery && (
        <div className="card">
          <div className="card__head">
            <Icon.Layers size={15} className="muted" />
            <h3 className="card__title">Final query</h3>
            <span className="card__sub">read-only · edit in legacy editor</span>
          </div>
          <div className="card__body">
            <pre
              className="sql__pre"
              style={{
                padding: 12,
                background: 'var(--surface-2)',
                borderRadius: 6,
                fontSize: 12,
                overflow: 'auto',
                whiteSpace: 'pre-wrap',
                maxHeight: 240,
              }}
            >
              {state.finalQuery}
            </pre>
          </div>
        </div>
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
