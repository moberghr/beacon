import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { SqlEditor, type MonacoEditorLike } from '@/components/ui/SqlEditor';
import { DatabaseExplorer } from '@/components/ui/DatabaseExplorer';
import {
  useDataSourceMetadataQuery,
  useDataSourcesQuery,
} from '@/routes/data-sources/queries';
import {
  PARAMETER_TYPE,
  PARAMETER_TYPE_LABEL,
  type ParameterTypeId,
  type UpdateQueryStepPayload,
  type UpdateQueryStepParameterPayload,
  useCreateQuery,
  useUpdateQueryMutation,
} from './queries';

/**
 * `/queries/new` — full create-query form. Mirrors the Beacon-design
 * `add-query.jsx` page in functional terms. Phase 3 batch 5g.
 *
 * Save flow is two-step (atomic from the user's POV):
 *   1. POST /beacon/api/queries  → returns the new queryId
 *   2. PUT  /beacon/api/queries/{id} → pushes steps + parameters
 *
 * If step 2 fails the new query exists as a placeholder the user can
 * recover from `/queries/{id}/edit`. We surface the error and keep the
 * page mounted with the id so the user can retry without losing state.
 *
 * Run is intentionally disabled here — the preview endpoint requires a
 * saved query id. After Save, we navigate to the detail page where Run
 * is available.
 *
 * Deferred (vs design):
 *  - Database explorer side panel inside the SQL card
 *  - Live ping/health indicator next to the data-source select
 *  - Diagram/JSON segmented control on the Query flow card
 *  - Variables button in the builder card head
 *  - Auto-save (the page is explicit-save only)
 */

interface DraftStep {
  // Local draft id used as React key while the step has no real stepId.
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

const PARAM_REGEX = /\{(\w+)\}/g;

function detectParameters(sql: string, existing: DraftParameter[]): DraftParameter[] {
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
  // Update mutation is keyed by queryId; we feed it the id we just got.
  const [createdId, setCreatedId] = useState<number | null>(null);
  const update = useUpdateQueryMutation(createdId ?? undefined);

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [steps, setSteps] = useState<DraftStep[]>(() => [emptyStep(1)]);

  const dataSourceOptions = dataSources.data?.entries ?? [];

  // Hydrate the first step's data source once the dropdown loads. Only
  // touches steps that still have dataSourceId === 0 so user picks stick.
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

  const checks = [
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
  ] as const;
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
    const raw = window.prompt('Parameter name (without braces):', '');
    if (!raw) return;
    const cleaned = raw.trim().replace(/^\{|\}$/g, '');
    if (!cleaned || !/^\w+$/.test(cleaned)) {
      toast.error('Parameter name must be alphanumeric.');
      return;
    }
    setSteps(prev =>
      prev.map(s => {
        if (s.draftId !== draftId) return s;
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

  const onSave = async () => {
    if (!canSave) return;
    try {
      const created = await create.mutateAsync({
        name: trimmedName,
        description: trimmedDesc || null,
      });
      const newId = created.queryId;
      setCreatedId(newId);

      // Push full payload only if we have meaningful content beyond name +
      // description. Otherwise the placeholder is fine and the user lands
      // on the editor to fill it in.
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
      toast.success('Query created');
      navigate(`/queries/${newId}`);
    } catch {
      // Toasts already raised by the mutation hooks.
    }
  };

  // Cmd+S to save, Cmd+Enter is reserved for Run (disabled here).
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        void onSave();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name, description, steps, canSave]);

  return (
    <div className="page" data-screen-label="02 Add New Query">
      <div className="page-hero">
        <div className="page-hero__inner">
          <div className="page-hero__main">
            <div className="page-hero__eyebrow">
              <Link to="/queries" style={{ color: 'inherit', textDecoration: 'none' }}>
                Queries
              </Link>
              <span className="beacon-hero__sep">/</span>
              <span>NEW</span>
              <span className="beacon-hero__sep">·</span>
              <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>DRAFT</span>
            </div>
            <h1 className="page-hero__title">
              Compose a <span className="page-hero__word">cross-source</span> query.
            </h1>
            <p className="page-hero__sub">
              Chain SQL steps across data sources, pipe results between steps using{' '}
              <span className="mono">@result1</span>, and parameterize with{' '}
              <span className="mono">{`{name}`}</span>.
            </p>
          </div>
          <div className="page-hero__actions">
            <button type="button" className="btn" onClick={() => navigate('/queries')}>
              <span className="btn__icon" style={{ transform: 'rotate(180deg)', display: 'inline-flex' }}>
                <Icon.Chevron size={14} />
              </span>
              Back
            </button>
            <button
              type="button"
              className="btn"
              disabled
              title="Save first to run"
            >
              <Icon.Bolt size={14} className="btn__icon" /> Run
            </button>
            <button
              type="button"
              className="btn btn--primary"
              onClick={onSave}
              disabled={!canSave}
            >
              <Icon.Check size={14} className="btn__icon" />
              {create.isPending || update.isPending ? 'Saving…' : 'Save query'}
            </button>
          </div>
        </div>
      </div>

      <div className="q-layout">
        <div className="q-section">
          <div className="card">
            <div className="card__head">
              <Icon.Info size={15} className="muted" />
              <h3 className="card__title">Query details</h3>
              <div className="card__actions">
                <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>UNSAVED</span>
              </div>
            </div>
            <div className="card__body">
              <div className="q-meta-grid">
                <div className="q-field q-field--full">
                  <label className="q-label" htmlFor="new-query-name">
                    Query name <span className="q-label__req">*</span>
                  </label>
                  <input
                    id="new-query-name"
                    className="q-input"
                    placeholder="e.g. EU signups vs revenue — daily"
                    value={name}
                    maxLength={200}
                    onChange={e => setName(e.target.value)}
                  />
                  <span className="q-help">A short, descriptive name. Shown in lists and notifications.</span>
                </div>
                <div className="q-field q-field--full">
                  <label className="q-label" htmlFor="new-query-desc">
                    Description <span className="q-label__req">*</span>
                  </label>
                  <textarea
                    id="new-query-desc"
                    className="q-textarea"
                    placeholder="What does this query measure, and why? Include thresholds and audience."
                    value={description}
                    maxLength={1000}
                    onChange={e => setDescription(e.target.value)}
                  />
                  <span className="q-help">{description.length}/280 · plain text.</span>
                </div>
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Cross-data-source query builder</h3>
              <span className="card__sub">
                {steps.length} step{steps.length === 1 ? '' : 's'} · {totalParams} param{totalParams === 1 ? '' : 's'}
              </span>
              <div className="card__actions">
                <button type="button" className="btn btn--primary" onClick={addStep}>
                  <Icon.Plus size={14} className="btn__icon" /> Add step
                </button>
              </div>
            </div>
            <div className="card__body">
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                {steps.map((step, idx) => {
                  const ds = dataSourceLookup.get(step.dataSourceId);
                  return (
                    <div key={step.draftId} className="step-card">
                      <div className="step-card__head">
                        <span className="step-num">{String(idx + 1).padStart(2, '0')}</span>
                        <span className="step-name">Step {idx + 1}</span>
                        <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>not run</span>
                        <div style={{ marginLeft: 'auto', display: 'flex', gap: 4 }}>
                          <button
                            type="button"
                            className="icon-btn"
                            title="Remove step"
                            onClick={() => removeStep(step.draftId)}
                            disabled={steps.length === 1}
                          >
                            <Icon.X size={14} />
                          </button>
                        </div>
                      </div>
                      <div className="step-card__body">
                        <div className="step-row">
                          <div className="q-field">
                            <label className="q-label">Step name</label>
                            <input
                              className="q-input"
                              value={step.name}
                              onChange={e => updateStep(step.draftId, { name: e.target.value })}
                            />
                          </div>
                          <div className="q-field">
                            <label className="q-label">Target database</label>
                            <select
                              className="q-select"
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
                            </select>
                            {ds && (
                              <span className="q-help mono" style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
                                {ds.engine}
                              </span>
                            )}
                          </div>
                        </div>

                        <div>
                          <div className="q-label" style={{ marginBottom: 4 }}>SQL query</div>
                          <div className="q-help" style={{ marginBottom: 8 }}>
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

                        <div className="params">
                          <div className="params__head">
                            <div className="q-label" style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                              Parameters
                              <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>
                                {step.parameters.length === 0 ? 'none' : `${step.parameters.length} detected`}
                              </span>
                            </div>
                            <button
                              type="button"
                              className="btn btn--ghost"
                              onClick={() => onRescan(step.draftId)}
                            >
                              <Icon.Refresh size={13} className="btn__icon" /> Re-scan
                            </button>
                          </div>
                          <div
                            style={{
                              display: 'flex',
                              flexWrap: 'wrap',
                              gap: 8,
                              marginTop: 8,
                              alignItems: 'center',
                            }}
                          >
                            {step.parameters.map(p => (
                              <span key={p.name} className="param-chip">
                                <span>{`{${p.name}}`}</span>
                                <select
                                  className="param-chip__type"
                                  value={p.type}
                                  onChange={e => {
                                    const nextType = Number(e.target.value) as ParameterTypeId;
                                    updateStep(step.draftId, {
                                      parameters: step.parameters.map(x =>
                                        x.name === p.name ? { ...x, type: nextType } : x,
                                      ),
                                    });
                                  }}
                                  style={{
                                    border: 'none',
                                    background: 'transparent',
                                    font: 'inherit',
                                    color: 'inherit',
                                    cursor: 'pointer',
                                  }}
                                >
                                  {Object.entries(PARAMETER_TYPE_LABEL).map(([k, label]) => (
                                    <option key={k} value={k}>{label}</option>
                                  ))}
                                </select>
                              </span>
                            ))}
                            <button
                              type="button"
                              className="chip"
                              onClick={() => onAddParameter(step.draftId)}
                            >
                              <Icon.Plus size={11} /> add parameter
                            </button>
                          </div>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Query flow</h3>
              <span className="card__sub">step graph</span>
            </div>
            <div className="flow">
              {steps.map((step, idx) => {
                const ds = dataSourceLookup.get(step.dataSourceId);
                const showResult = idx < steps.length - 1;
                return (
                  <div
                    key={step.draftId}
                    style={{ display: 'contents' }}
                  >
                    <div className="flow__node flow__node--db">
                      <div className="flow__node-title">
                        STEP {idx + 1} · {(step.name || `STEP ${idx + 1}`).toUpperCase()}
                      </div>
                      <div className="flow__node-sub">
                        {ds ? `${ds.name} · ${ds.engine}` : 'no source'}
                      </div>
                    </div>
                    <div className="flow__edge"></div>
                    {showResult && (
                      <>
                        <div className="flow__node flow__node--result">
                          <div className="flow__node-title">@result{idx + 1}</div>
                          <div className="flow__node-sub">intermediate result</div>
                        </div>
                        <div className="flow__edge"></div>
                      </>
                    )}
                  </div>
                );
              })}
              <button type="button" className="flow__plus" onClick={addStep}>
                <Icon.Plus size={12} /> Pipe to next step
              </button>
            </div>
          </div>
        </div>

        <aside className="q-aside">
          <div className="card">
            <div className="card__head">
              <Icon.Info size={15} className="muted" />
              <h3 className="card__title">Query info</h3>
            </div>
            <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
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
              <InfoRow label="Last edited" value="just now" />
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Check size={15} className="muted" />
              <h3 className="card__title">Pre-flight checks</h3>
              <div className="card__actions">
                <span className={'pill ' + (failing > 0 ? 'pill--warn' : 'pill--ok')}>
                  {failing > 0 ? `${failing} to fix` : 'all green'}
                </span>
              </div>
            </div>
            <div className="checks">
              {checks.map(c => (
                <Check key={c.title} tone={c.tone} title={c.title} detail={c.detail} />
              ))}
            </div>
          </div>

          <div className="callout">
            <Icon.Lightbulb size={16} className="callout__icon" />
            <div>
              <div className="callout__title">Tip · pipe results</div>
              <div className="callout__sub">
                Reference a previous step using <span className="mono">@result1</span> in your{' '}
                <span className="mono">FROM</span> clause to chain across sources.
              </div>
            </div>
          </div>
        </aside>
      </div>

      <div className="save-bar">
        <span className="save-bar__hint">
          <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>DRAFT</span>
          <span>Click a table in the explorer to insert it at the cursor.</span>
        </span>
        <div className="spacer"></div>
        <span className="save-bar__hint">
          <span>Press</span>
          <span className="kbd">⌘</span><span className="kbd">↵</span>
          <span>to run ·</span>
          <span className="kbd">⌘</span><span className="kbd">S</span>
          <span>to save</span>
        </span>
        <button type="button" className="btn" disabled title="Save first to run">
          <Icon.Bolt size={14} className="btn__icon" /> Run
        </button>
        <button
          type="button"
          className="btn btn--primary"
          onClick={onSave}
          disabled={!canSave}
        >
          <Icon.Check size={14} className="btn__icon" />
          {create.isPending || update.isPending ? 'Saving…' : 'Save query'}
        </button>
      </div>
    </div>
  );
}

function InfoRow({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
      <span style={{ fontSize: 12, color: 'var(--text-muted)', minWidth: 92 }}>{label}</span>
      <span className="mono" style={{ fontSize: 13, color: 'var(--text)', fontWeight: 500 }}>{value}</span>
      {detail && (
        <span className="mono subtle" style={{ fontSize: 11.5, marginLeft: 'auto' }}>{detail}</span>
      )}
    </div>
  );
}

interface NewStepEditorWithExplorerProps {
  draftId: number;
  dataSourceId: number;
  sqlValue: string;
  onSqlChange: (sql: string) => void;
  parameterNames: string[];
  crossStepResultCount: number;
}

function NewStepEditorWithExplorer({
  draftId,
  dataSourceId,
  sqlValue,
  onSqlChange,
  parameterNames,
  crossStepResultCount,
}: NewStepEditorWithExplorerProps) {
  const editorRef = useRef<MonacoEditorLike | null>(null);
  const metadataQuery = useDataSourceMetadataQuery(
    dataSourceId > 0 ? dataSourceId : null,
  );

  const insertAtCursor = (text: string) => {
    const editor = editorRef.current;
    if (!editor) return;
    const position = editor.getPosition();
    if (!position) return;
    editor.executeEdits('database-explorer', [
      {
        range: {
          startLineNumber: position.lineNumber,
          startColumn: position.column,
          endLineNumber: position.lineNumber,
          endColumn: position.column,
        },
        text,
        forceMoveMarkers: true,
      },
    ]);
    editor.focus();
  };

  return (
    <div className="sql-explorer-shell">
      <DatabaseExplorer dataSourceId={dataSourceId} onInsert={insertAtCursor} />
      <div className="sql-explorer-shell__editor">
        <SqlEditor
          id={`new-step-${draftId}-sql`}
          height={280}
          value={sqlValue}
          onChange={onSqlChange}
          metadata={metadataQuery.data ?? null}
          parameterNames={parameterNames}
          crossStepResultCount={crossStepResultCount}
          onEditorReady={editor => {
            editorRef.current = editor;
          }}
        />
      </div>
    </div>
  );
}

function Check({
  tone,
  title,
  detail,
}: {
  tone: 'ok' | 'warn' | 'pending';
  title: string;
  detail?: string;
}) {
  const ic =
    tone === 'ok' ? <Icon.Check size={11} /> :
    tone === 'warn' ? <Icon.Alert size={11} /> :
    <Icon.Clock size={11} />;
  return (
    <div className="check">
      <div className={`check__icon check__icon--${tone}`}>{ic}</div>
      <div>
        <div className="check__main">{title}</div>
        {detail && <div className="check__detail">{detail}</div>}
      </div>
    </div>
  );
}
