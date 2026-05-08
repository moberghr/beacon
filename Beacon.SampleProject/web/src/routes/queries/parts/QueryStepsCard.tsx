import { Icon } from '@/components/Icon';
import type { QueryDetail, QueryStep } from '../queries';

interface QueryStepsCardProps {
  query: QueryDetail;
  /** Path to the legacy Blazor edit screen; used until Phase 5f ships the React editor. */
  legacyEditHref: string;
}

export function QueryStepsCard({ query, legacyEditHref }: QueryStepsCardProps) {
  const steps = [...query.steps].sort((a, b) => a.stepOrder - b.stepOrder);
  const totalParams = steps.reduce((sum, s) => sum + s.parameters.length, 0);
  const stepsLabel = `${steps.length} step${steps.length === 1 ? '' : 's'} · ${totalParams} param${totalParams === 1 ? '' : 's'}`;

  return (
    <div className="card">
      <div className="card__head">
        <Icon.Branch size={15} className="muted" />
        <h3 className="card__title">Query steps</h3>
        <span className="card__sub">{stepsLabel}</span>
        <div className="card__actions">
          <a className="btn btn--primary" href={legacyEditHref}>
            <Icon.Sliders size={13} className="btn__icon" /> Edit in legacy editor
          </a>
        </div>
      </div>
      <div className="card__body">
        {steps.length === 0
          ? <span className="muted">No steps configured.</span>
          : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {steps.map(step => <StepRow key={step.stepId} step={step} />)}
            </div>
          )}
      </div>
    </div>
  );
}

function StepRow({ step }: { step: QueryStep }) {
  const stepNum = String(step.stepOrder).padStart(2, '0');
  const paramCount = step.parameters.length;

  return (
    <div className="step-card">
      <div className="step-card__head">
        <span className="step-num">{stepNum}</span>
        <span className="step-name">{step.name}</span>
        <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>read-only</span>
        <span className="mono subtle" style={{ fontSize: 11.5, marginLeft: 8 }}>
          {step.dataSourceName} · {step.databaseEngineDescription}
        </span>
      </div>
      <div className="step-card__body">
        <div>
          <div className="q-label" style={{ marginBottom: 8, display: 'flex', alignItems: 'center', gap: 8 }}>
            SQL query
            <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>read-only</span>
          </div>
          <pre className="sql__pre" style={{ padding: 12, background: 'var(--surface-2)', borderRadius: 6, fontSize: 12, overflow: 'auto', whiteSpace: 'pre-wrap', maxHeight: 240 }}>
            {step.sqlValue}
          </pre>
        </div>
        <div className="params">
          <div className="params__head">
            <div className="q-label" style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
              Parameters
              <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>
                {paramCount === 0 ? 'none' : `${paramCount} detected`}
              </span>
            </div>
          </div>
          {paramCount > 0 && (
            <div className="params__chips">
              {step.parameters.map(p => (
                <span key={p.name} className="chip mono">
                  @{p.name}
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
