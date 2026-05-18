import { Link } from 'react-router-dom';
import { GitBranch, SlidersHorizontal } from 'lucide-react';
import {
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Button,
  Pill,
} from '@/components/beacon';
import type { QueryDetail, QueryStep } from '../queries';

interface QueryStepsCardProps {
  query: QueryDetail;
  /**
   * Phase 5f: edit goes to the in-app React editor (`/queries/:id/edit`).
   * `react-router` prefixes the basename, so callers pass a path-only string
   * and we render via `<Link>` (never `/app/...`).
   */
  editHref: string;
}

export function QueryStepsCard({ query, editHref }: QueryStepsCardProps) {
  const steps = [...query.steps].sort((a, b) => a.stepOrder - b.stepOrder);
  const totalParams = steps.reduce((sum, s) => sum + s.parameters.length, 0);
  const stepsLabel = `${steps.length} step${steps.length === 1 ? '' : 's'} · ${totalParams} param${totalParams === 1 ? '' : 's'}`;

  return (
    <Card>
      <CardHead>
        <GitBranch className="size-3.5 text-text-muted" />
        <CardTitle>Query steps</CardTitle>
        <CardSub>{stepsLabel}</CardSub>
        <CardActions>
          <Link to={editHref}>
            <Button variant="primary" icon={<SlidersHorizontal />}>Edit</Button>
          </Link>
        </CardActions>
      </CardHead>
      <CardBody>
        {steps.length === 0
          ? <span className="text-text-muted">No steps configured.</span>
          : (
            <div className="flex flex-col gap-3">
              {steps.map(step => <StepRow key={step.stepId} step={step} />)}
            </div>
          )}
      </CardBody>
    </Card>
  );
}

function StepRow({ step }: { step: QueryStep }) {
  const stepNum = String(step.stepOrder).padStart(2, '0');
  const paramCount = step.parameters.length;

  return (
    <div className="border border-border rounded-md overflow-hidden bg-surface">
      <div className="flex items-center gap-2 px-3 py-2 bg-surface-2 border-b border-border">
        <span className="mono text-2xs font-semibold px-1.5 py-0.5 rounded-xs bg-surface border border-border-strong">
          {stepNum}
        </span>
        <span className="text-sm font-medium">{step.name}</span>
        <Pill className="mono normal-case tracking-normal">read-only</Pill>
        <span className="mono text-text-subtle text-xs ml-2">
          {step.dataSourceName} · {step.databaseEngineDescription}
        </span>
      </div>
      <div className="p-3 flex flex-col gap-3">
        <div>
          <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-2 inline-flex items-center gap-2">
            SQL query
            <Pill className="mono normal-case tracking-normal">read-only</Pill>
          </div>
          <pre className="m-0 p-3 bg-surface-2 rounded-sm mono text-xs overflow-auto whitespace-pre-wrap max-h-60 border border-border">
            {step.sqlValue}
          </pre>
        </div>
        <div>
          <div className="flex items-center justify-between mb-2">
            <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted inline-flex items-center gap-2">
              Parameters
              <Pill className="mono normal-case tracking-normal">
                {paramCount === 0 ? 'none' : `${paramCount} detected`}
              </Pill>
            </div>
          </div>
          {paramCount > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {step.parameters.map(p => (
                <span
                  key={p.name}
                  className="mono text-xs px-2 py-0.5 rounded-xs bg-surface-2 border border-border"
                >
                  {p.placeholder ?? `{${p.name}}`}
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
