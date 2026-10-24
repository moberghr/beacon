import { useEffect, useState } from 'react';
import { History, Hourglass, Info } from 'lucide-react';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Dialog } from '@/components/ui/Dialog';
import { Banner, Button, Field, Pill, Textarea } from '@/components/beacon';
import { AiActorPlanStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import { PlanActionsView } from './PlanActionsView';
import {
  PLAN_STATUS_LABEL,
  useAiActorPlanQuery,
  useApprovePlan,
  useRejectPlan,
  useRequestPlanRevision,
  type AiActorPlan,
} from './queries';

const MAX_COMMENT = 2000;

// How far back the dialog follows a plan's earlier versions.
const MAX_HISTORY = 10;

type Decision = 'approve' | 'reject' | 'revise';

interface PlanReviewDialogProps {
  open: boolean;
  actorId: number;
  planId: number | null;
  onClose: () => void;
}

/**
 * One plan an AI actor proposed: its analysis, findings and actions (with their SQL, read-only), its earlier versions,
 * and the decisions the server says the caller may take. Approving runs the actions; rejecting closes the plan with a
 * reason; a revision sends the caller's feedback back to the actor, which proposes a new version.
 */
export function PlanReviewDialog({ open, actorId, planId, onClose }: PlanReviewDialogProps) {
  const plan = useAiActorPlanQuery(planId ?? undefined);
  const approve = useApprovePlan(actorId);
  const reject = useRejectPlan(actorId);
  const revise = useRequestPlanRevision(actorId);
  const [comment, setComment] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [confirmingApprove, setConfirmingApprove] = useState(false);

  useEffect(() => {
    if (open) {
      setComment('');
      setError(null);
      setConfirmingApprove(false);
    }
  }, [open, planId]);

  const data = plan.data;
  const isPending = data?.status === AiActorPlanStatus.PendingApproval && !data.isExpired;
  const mayApprove = isPending && data?.mayApprove === true;
  const mayRejectOrRevise = isPending && data?.mayRejectOrRevise === true;
  const busy = approve.isPending || reject.isPending || revise.isPending;

  const decide = async (decision: Decision) => {
    if (planId == null) return;
    setConfirmingApprove(false);
    const text = comment.trim();
    if (text.length > MAX_COMMENT) {
      setError(`Max ${MAX_COMMENT} characters`);
      return;
    }
    if (decision !== 'approve' && !text) {
      setError(decision === 'reject' ? 'Say why the plan is rejected.' : 'Say what the actor should change.');
      return;
    }
    setError(null);
    try {
      if (decision === 'approve') {
        await approve.mutateAsync({ planId, comment: text || null });
      } else if (decision === 'reject') {
        await reject.mutateAsync({ planId, reason: text });
      } else {
        await revise.mutateAsync({ planId, feedback: text });
      }
      onClose();
    } catch {
      // The mutations (createSimpleMutation) already toast the error.
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Review plan"
      sub="What the AI actor proposes to do. Nothing runs until the plan is approved."
      size="lg"
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Close</Button>
          {mayRejectOrRevise && (
            <>
              <Button onClick={() => decide('revise')} disabled={busy}>
                {revise.isPending ? 'Requesting…' : 'Request revision'}
              </Button>
              <Button variant="danger" onClick={() => decide('reject')} disabled={busy}>
                {reject.isPending ? 'Rejecting…' : 'Reject'}
              </Button>
            </>
          )}
          {mayApprove && (
            <Button variant="primary" onClick={() => setConfirmingApprove(true)} disabled={busy}>
              {approve.isPending ? 'Approving…' : 'Approve and run'}
            </Button>
          )}
        </>
      }
    >
      {plan.isLoading && <div className="text-text-muted">Loading plan…</div>}
      {plan.isError && (
        <div className="text-xs text-crit">{plan.error instanceof Error ? plan.error.message : 'Failed to load the plan'}</div>
      )}
      {data && (
        <div className="flex flex-col gap-4">
          <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm m-0">
            <KV label="Actor" value={<span className="font-semibold">{data.actorName}</span>} />
            <KV label="Status" value={<StatusPill plan={data} />} />
            <KV label="Version" value={data.version} />
            <KV label="Proposed" value={formatDateTime(data.proposedAt)} />
            <KV label="Expires" value={formatDateTime(data.expiresAt)} />
            <KV label="Cost" value={`${data.tokensUsed} tokens · $${(data.estimatedCost ?? 0).toFixed(4)}`} />
          </dl>

          <PlanBanners plan={data} />

          {data.userInstruction && (
            <section>
              <h3 className="m-0 mb-1 text-sm font-semibold">Instruction</h3>
              <p className="m-0 text-sm whitespace-pre-wrap">{data.userInstruction}</p>
            </section>
          )}

          <section>
            <h3 className="m-0 mb-1 text-sm font-semibold">Analysis</h3>
            <p className="m-0 text-sm whitespace-pre-wrap">{data.analysis}</p>
          </section>

          {(data.findings ?? []).length > 0 && (
            <section>
              <h3 className="m-0 mb-1 text-sm font-semibold">Findings</h3>
              <ul className="m-0 pl-5 text-sm flex flex-col gap-0.5">
                {data.findings.map((x, index) => (
                  <li key={index}>{x}</li>
                ))}
              </ul>
            </section>
          )}

          <section>
            <h3 className="m-0 mb-2 text-sm font-semibold">Proposed actions</h3>
            <PlanActionsView actions={data.proposedActions ?? []} />
          </section>

          {data.parentPlanId != null && (
            <section aria-label="Earlier versions">
              <h3 className="m-0 mb-2 text-sm font-semibold flex items-center gap-1.5">
                <History className="size-3.5" /> Earlier versions
              </h3>
              <ul className="m-0 p-0 list-none flex flex-col">
                <EarlierVersion planId={data.parentPlanId} depth={1} />
              </ul>
            </section>
          )}

          {(mayApprove || mayRejectOrRevise) && (
            <div className="flex flex-col gap-1">
              <Field label="Comment">
                <Textarea
                  id="plan-review-comment"
                  rows={3}
                  value={comment}
                  onChange={e => setComment(e.target.value)}
                  placeholder="Optional when approving; the reason when rejecting, the feedback when asking for a revision."
                  aria-invalid={!!error}
                />
              </Field>
              {error && <span className="text-xs text-crit">{error}</span>}
            </div>
          )}
        </div>
      )}
      <ConfirmDialog
        open={confirmingApprove}
        title="Approve and run"
        message="Approving runs the plan's actions now (queries and subscriptions are created or changed, notifications are sent) as you. Each action is checked again before it runs."
        confirmLabel="Approve and run"
        busy={approve.isPending}
        onConfirm={() => decide('approve')}
        onCancel={() => setConfirmingApprove(false)}
      />
    </Dialog>
  );
}

function StatusPill({ plan }: { plan: AiActorPlan }) {
  if (plan.isExpired) {
    return <Pill tone="neutral">Expired</Pill>;
  }
  const status = PLAN_STATUS_LABEL[plan.status] ?? { label: String(plan.status), tone: 'neutral' as const };
  return <Pill tone={status.tone}>{status.label}</Pill>;
}

function PlanBanners({ plan }: { plan: AiActorPlan }) {
  if (plan.isExpired) {
    return (
      <Banner
        tone="warn"
        icon={<Hourglass />}
        title="This plan expired"
        sub={`It was not decided before ${formatDateTime(plan.expiresAt)}, so it can no longer be approved, rejected or revised.`}
      />
    );
  }

  if (plan.status !== AiActorPlanStatus.PendingApproval) {
    return (
      <Banner
        tone="info"
        icon={<Info />}
        title="This plan was decided"
        sub={
          <>
            {plan.reviewedAt ? `Decided ${formatDateTime(plan.reviewedAt)}.` : 'It is no longer pending.'}
            {plan.reviewerComment ? <> Reviewer’s comment: “{plan.reviewerComment}”</> : null}
          </>
        }
      />
    );
  }

  if (!plan.mayApprove && plan.approveBlockedReason) {
    return <Banner tone="info" icon={<Info />} title="You cannot approve this plan" sub={plan.approveBlockedReason} />;
  }

  return null;
}

// One earlier version of the plan (the one it revised), then the one before it.
function EarlierVersion({ planId, depth }: { planId: number; depth: number }) {
  const { data, isLoading, isError } = useAiActorPlanQuery(planId);

  if (isLoading) {
    return <li className="py-1.5 text-xs text-text-muted">Loading…</li>;
  }
  if (isError || !data) {
    return <li className="py-1.5 text-xs text-text-muted">An earlier version could not be loaded.</li>;
  }

  return (
    <>
      <li className="flex flex-col gap-0.5 py-2 border-b border-border last:border-b-0 text-sm">
        <span className="flex items-center gap-2">
          <span className="text-xs font-semibold">Version {data.version}</span>
          <StatusPill plan={data} />
          <span className="text-xs text-text-muted">
            proposed {formatDateTime(data.proposedAt)}
            {data.reviewedAt ? ` · decided ${formatDateTime(data.reviewedAt)}` : ''}
          </span>
        </span>
        {data.reviewerComment && <span className="text-xs">“{data.reviewerComment}”</span>}
      </li>
      {data.parentPlanId != null && depth < MAX_HISTORY && <EarlierVersion planId={data.parentPlanId} depth={depth + 1} />}
    </>
  );
}

function KV({ label, value }: { label: React.ReactNode; value: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1 border-b border-dashed border-border last:border-b-0">
      <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</dt>
      <dd className="m-0 text-sm text-right">{value}</dd>
    </div>
  );
}
