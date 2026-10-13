import { Info } from 'lucide-react';
import { Pill, type PillProps } from '@/components/beacon';
import type { ParameterType } from '@/lib/enums';
import { PARAMETER_TYPE_LABEL } from '@/routes/queries/queries';
import {
  StepDiffType,
  type ApprovalRequestDetail,
  type QueryStepParameterSnapshot,
  type QueryStepSnapshot,
  type StepDiff,
} from './queries';

const DIFF_LABEL: Record<StepDiffType, string> = {
  [StepDiffType.Unchanged]: 'Unchanged',
  [StepDiffType.Modified]: 'Modified',
  [StepDiffType.Added]: 'Added',
  [StepDiffType.Removed]: 'Removed',
};

const DIFF_TONE: Record<StepDiffType, PillProps['tone']> = {
  [StepDiffType.Unchanged]: 'neutral',
  [StepDiffType.Modified]: 'warn',
  [StepDiffType.Added]: 'ok',
  [StepDiffType.Removed]: 'crit',
};

/**
 * The steps a reviewer compares, position by position as the server pairs them. A query without an active version (a
 * new query) has nothing to compare with: every proposed step is added.
 */
export function stepDiffsOf(detail: ApprovalRequestDetail): StepDiff[] {
  if (detail.autoDiff) {
    return detail.autoDiff.stepDiffs ?? [];
  }

  return [...(detail.proposedVersion.steps ?? [])]
    .sort((a, b) => a.stepOrder - b.stepOrder)
    .map((step, index) => ({
      stepOrder: index + 1,
      diffType: StepDiffType.Added,
      stepA: null,
      stepB: step,
    }));
}

/**
 * What an approval would put live, against what is live now: each step's data source, SQL and parameters, and the
 * final query. This is the content the approval is bound to (the detail's content hash).
 */
export function ApprovalChangeDiff({ detail }: { detail: ApprovalRequestDetail }) {
  const diffs = stepDiffsOf(detail);
  const isNewQuery = detail.autoDiff == null;
  const current = isNewQuery ? null : detail.currentActiveVersion;
  const proposed = detail.proposedVersion;
  const nameChanged = !isNewQuery && (detail.autoDiff?.nameChanged ?? false);
  const descriptionChanged = !isNewQuery && (detail.autoDiff?.descriptionChanged ?? false);

  return (
    <div className="flex flex-col gap-3">
      {isNewQuery && (
        <div className="text-xs text-text-muted">
          The query has no active version yet: everything below is added by this change.
        </div>
      )}

      {(nameChanged || descriptionChanged) && (
        <div className="flex items-start gap-2 text-xs text-text-muted">
          <Info className="size-3.5 mt-0.5 shrink-0" />
          <span>
            {nameChanged && (
              <>
                Name: <span className="text-text">{current?.name ?? '—'}</span> →{' '}
                <span className="text-text">{proposed.name}</span>.{' '}
              </>
            )}
            {descriptionChanged && 'The description changes. '}
            Name and description go live with the approval; they are not part of the reviewed SQL.
          </span>
        </div>
      )}

      {diffs.length === 0 && <div className="text-sm text-text-muted">No steps.</div>}

      {diffs.map(diff => (
        <StepDiffBlock key={diff.stepOrder} diff={diff} />
      ))}

      <FinalQueryDiff current={current?.finalQuery ?? null} proposed={proposed.finalQuery ?? null} />
    </div>
  );
}

function StepDiffBlock({ diff }: { diff: StepDiff }) {
  const a = diff.stepA;
  const b = diff.stepB;
  const label = b?.name || a?.name;
  const sourceChanged = a != null && b != null && a.dataSourceId !== b.dataSourceId;
  const nameChanged = a != null && b != null && (a.name ?? '') !== (b.name ?? '');
  const sqlChanged = a != null && b != null && a.sqlValue !== b.sqlValue;
  const parametersChanged = a != null && b != null && parameterKey(a) !== parameterKey(b);

  return (
    <section aria-label={`Step ${diff.stepOrder}`} className="border border-border rounded-sm p-3">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-xs font-semibold">Step {diff.stepOrder}</span>
        {label && <span className="text-xs text-text-muted">{label}</span>}
        <Pill tone={DIFF_TONE[diff.diffType] ?? 'neutral'}>{DIFF_LABEL[diff.diffType] ?? 'Changed'}</Pill>
      </div>

      <dl className="grid grid-cols-[110px_1fr] gap-x-3 gap-y-1 text-xs m-0 mb-2">
        <dt className="text-text-muted">Data source</dt>
        <dd className="m-0">
          {sourceChanged ? (
            <Change before={a.dataSourceName} after={b.dataSourceName} />
          ) : (
            (b ?? a)?.dataSourceName ?? '—'
          )}
        </dd>
        {nameChanged && (
          <>
            <dt className="text-text-muted">Step name</dt>
            <dd className="m-0">
              <Change before={a.name || '—'} after={b.name || '—'} />
            </dd>
          </>
        )}
      </dl>

      {diff.diffType === StepDiffType.Added && b && <SqlBlock label="Proposed SQL" sql={b.sqlValue} />}
      {diff.diffType === StepDiffType.Removed && a && <SqlBlock label="Removed SQL" sql={a.sqlValue} />}
      {a && b && sqlChanged && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-2">
          <SqlBlock label="Current SQL" sql={a.sqlValue} />
          <SqlBlock label="Proposed SQL" sql={b.sqlValue} />
        </div>
      )}
      {a && b && !sqlChanged && <SqlBlock label="SQL (unchanged)" sql={b.sqlValue} />}

      {parametersChanged ? (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-2 mt-2">
          <ParameterList label="Current parameters" parameters={a.parameters ?? []} />
          <ParameterList label="Proposed parameters" parameters={b.parameters ?? []} />
        </div>
      ) : (
        ((b ?? a)?.parameters ?? []).length > 0 && (
          <div className="mt-2">
            <ParameterList label="Parameters" parameters={(b ?? a)?.parameters ?? []} />
          </div>
        )
      )}
    </section>
  );
}

function FinalQueryDiff({ current, proposed }: { current: string | null; proposed: string | null }) {
  const before = current ?? '';
  const after = proposed ?? '';
  if (!before && !after) {
    return null;
  }

  const type = !before
    ? StepDiffType.Added
    : !after
      ? StepDiffType.Removed
      : before === after
        ? StepDiffType.Unchanged
        : StepDiffType.Modified;

  return (
    <section aria-label="Final query" className="border border-border rounded-sm p-3">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-xs font-semibold">Final query</span>
        <Pill tone={DIFF_TONE[type]}>{DIFF_LABEL[type]}</Pill>
      </div>
      {type === StepDiffType.Modified ? (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-2">
          <SqlBlock label="Current final query" sql={before} />
          <SqlBlock label="Proposed final query" sql={after} />
        </div>
      ) : type === StepDiffType.Removed ? (
        <SqlBlock label="Removed final query" sql={before} />
      ) : (
        <SqlBlock label={type === StepDiffType.Added ? 'Proposed final query' : 'Final query (unchanged)'} sql={after} />
      )}
    </section>
  );
}

function SqlBlock({ label, sql }: { label: string; sql: string }) {
  return (
    <div>
      <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">{label}</div>
      <pre className="bg-surface-2 border border-border rounded-sm p-2.5 text-xs m-0 whitespace-pre-wrap overflow-auto max-h-80 mono">
        {sql || <span className="text-text-muted">(empty)</span>}
      </pre>
    </div>
  );
}

function ParameterList({ label, parameters }: { label: string; parameters: QueryStepParameterSnapshot[] }) {
  return (
    <div>
      <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">{label}</div>
      {parameters.length === 0 ? (
        <div className="text-xs text-text-muted">None</div>
      ) : (
        <ul className="m-0 pl-0 list-none flex flex-col gap-0.5 text-xs">
          {parameters.map(x => (
            <li key={`${x.name}|${x.type}|${x.placeholder ?? ''}`} className="mono">
              {x.name} · {PARAMETER_TYPE_LABEL[x.type as ParameterType] ?? `type ${x.type}`}
              {x.placeholder ? ` · ${x.placeholder}` : ''}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Change({ before, after }: { before: string; after: string }) {
  return (
    <span>
      <span className="line-through text-text-muted">{before}</span> → <span className="font-medium">{after}</span>
    </span>
  );
}

// What a step binds: its parameters' names, types and placeholders, in a stable order.
function parameterKey(step: QueryStepSnapshot): string {
  return (step.parameters ?? [])
    .map(x => `${x.name}|${x.type}|${x.placeholder ?? ''}`)
    .sort()
    .join('\n');
}
