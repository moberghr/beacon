import { Lock } from 'lucide-react';
import { Pill } from '@/components/beacon';
import { AiActorActionType } from '@/lib/enums';
import { SqlBlock } from '@/routes/approvals/ApprovalChangeDiff';
import { ACTION_TYPE_LABEL, type ProposedAction } from './queries';

// The parameter a new query's SQL travels in; shown as SQL, not among the other parameters.
const SQL_PARAMETER = 'sql';

/**
 * What a plan would do, action by action: the action, the actor's reasoning, what it targets and the SQL it would store,
 * read-only. A refinement shows the query's SQL now next to the SQL the plan proposes.
 */
export function PlanActionsView({ actions }: { actions: ProposedAction[] }) {
  if (actions.length === 0) {
    return <div className="text-sm text-text-muted">The plan proposes no actions.</div>;
  }

  return (
    <ol className="m-0 p-0 list-none flex flex-col gap-3">
      {actions.map((x, index) => (
        <li key={index}>
          <ActionBlock index={index + 1} action={x} />
        </li>
      ))}
    </ol>
  );
}

function ActionBlock({ index, action }: { index: number; action: ProposedAction }) {
  const parameters = action.parameters ?? {};
  const sql = action.proposedSql ?? textOf(parameters[SQL_PARAMETER]);
  const others = Object.entries(parameters).filter(([key]) => key !== SQL_PARAMETER);
  const isRefinement = action.actionType === AiActorActionType.RefineQuery;
  const current = action.currentSql ?? '';

  return (
    <section aria-label={`Action ${index}`} className="border border-border rounded-sm p-3">
      <div className="flex flex-wrap items-center gap-2 mb-2">
        <span className="text-xs font-semibold">Action {index}</span>
        <Pill tone={isRefinement ? 'warn' : 'info'}>{ACTION_TYPE_LABEL[action.actionType] ?? `Action ${action.actionType}`}</Pill>
        {action.targetQueryName && <span className="text-xs text-text-muted">{action.targetQueryName}</span>}
        {action.isLocked && (
          <Pill tone="neutral" title="The target query is locked">
            <Lock className="size-3" /> Locked
          </Pill>
        )}
      </div>

      {action.reasoning && <p className="m-0 mb-2 text-sm whitespace-pre-wrap">{action.reasoning}</p>}

      {others.length > 0 && (
        <dl className="grid grid-cols-[140px_1fr] gap-x-3 gap-y-1 text-xs m-0 mb-2">
          {others.map(([key, value]) => (
            <div key={key} className="contents">
              <dt className="text-text-muted">{key}</dt>
              <dd className="m-0 mono break-all">{textOf(value) ?? '—'}</dd>
            </div>
          ))}
        </dl>
      )}

      {isRefinement && current && sql && current !== sql && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-2">
          <SqlBlock label="Current SQL" sql={current} />
          <SqlBlock label="Proposed SQL" sql={sql} />
        </div>
      )}
      {isRefinement && current && sql && current === sql && <SqlBlock label="SQL (unchanged)" sql={sql} />}
      {(!isRefinement || !current) && sql && <SqlBlock label="Proposed SQL" sql={sql} />}
      {isRefinement && current && !sql && <SqlBlock label="Current SQL" sql={current} />}
    </section>
  );
}

// A parameter as the server sent it: text as it is, anything else as JSON.
function textOf(value: unknown): string | null {
  if (value == null) {
    return null;
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}
