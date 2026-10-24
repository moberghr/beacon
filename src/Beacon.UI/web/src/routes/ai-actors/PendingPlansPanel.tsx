import { useState } from 'react';
import { ClipboardCheck } from 'lucide-react';
import { Button, Card, CardBody, Pill } from '@/components/beacon';
import { formatDateTime, formatNumber } from '@/lib/format';
import { PlanReviewDialog } from './PlanReviewDialog';
import { usePendingPlansQuery } from './queries';

interface PendingPlansPanelProps {
  actorId: number;
  /** The actor proposes plans for approval instead of running its actions. */
  requiresApproval: boolean;
}

/** The plans the actor proposed that wait for a decision, newest first; each opens in the review dialog. */
export function PendingPlansPanel({ actorId, requiresApproval }: PendingPlansPanelProps) {
  const { data, isLoading, isError, error } = usePendingPlansQuery(actorId);
  const [reviewing, setReviewing] = useState<number | null>(null);
  const plans = data ?? [];

  // An actor that runs its actions without approval proposes no plans: the panel shows only while one still waits.
  if (!requiresApproval && plans.length === 0) {
    return null;
  }

  return (
    <Card>
      <CardBody>
        <h2 className="m-0 mb-2 text-base font-semibold text-text flex items-center gap-2">
          <ClipboardCheck className="size-4" /> Plans waiting for approval
          {plans.length > 0 && <Pill tone="warn">{plans.length}</Pill>}
        </h2>
        {isLoading && <p className="m-0 text-sm text-text-muted">Loading plans…</p>}
        {isError && (
          <p className="m-0 text-sm text-crit">{error instanceof Error ? error.message : 'Failed to load the plans'}</p>
        )}
        {!isLoading && !isError && plans.length === 0 && (
          <p className="m-0 text-sm text-text-muted">No plan waits for a decision.</p>
        )}
        {plans.length > 0 && (
          <ul className="m-0 p-0 list-none flex flex-col">
            {plans.map(x => (
              <li
                key={x.planId}
                className="flex items-start justify-between gap-3 py-2 border-b border-border last:border-b-0 text-sm"
              >
                <div className="flex flex-col gap-0.5 min-w-0">
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold">Version {x.version}</span>
                    <span className="text-xs text-text-muted">
                      {formatNumber(x.actionCount)} {x.actionCount === 1 ? 'action' : 'actions'} · proposed{' '}
                      {formatDateTime(x.proposedAt)} · expires {formatDateTime(x.expiresAt)}
                    </span>
                  </span>
                  <span className="text-xs line-clamp-2">{x.analysis}</span>
                </div>
                <Button type="button" onClick={() => setReviewing(x.planId)}>
                  Review
                </Button>
              </li>
            ))}
          </ul>
        )}
      </CardBody>
      {reviewing != null && (
        <PlanReviewDialog open actorId={actorId} planId={reviewing} onClose={() => setReviewing(null)} />
      )}
    </Card>
  );
}
