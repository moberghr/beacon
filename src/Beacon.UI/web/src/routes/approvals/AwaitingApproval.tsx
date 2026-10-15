import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { toast } from 'sonner';
import { Hourglass, XCircle } from 'lucide-react';
import { Banner, Button, Pill } from '@/components/beacon';
import { useAccess } from '@/auth/useAuth';
import { ApprovalStatus, ChangeApprovalKind } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import type { ChangeApprovalOutcome } from './queries';

/** What waits for approval on a governed resource: its pending request, if one waits, or else the last decision on it. */
export interface AwaitingApprovalState {
  requestId?: number | null;
  lastDecision?: ChangeApprovalOutcome | null;
}

/** What to do about a rejected change: a migration job cannot be edited, so it is deleted and created again. */
export const REJECTED_REMEDY: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'Rejected — delete and recreate the job',
  [ChangeApprovalKind.CustomSqlRules]: 'Rejected — change it and save again',
  [ChangeApprovalKind.RecipientDestination]: 'Rejected — change it and save again',
};

const REJECTED_EFFECT: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'A reviewer rejected this job’s definition, so it never runs as it is.',
  [ChangeApprovalKind.CustomSqlRules]: 'A reviewer rejected the change of these Custom SQL rules, so they are not evaluated as they are.',
  [ChangeApprovalKind.RecipientDestination]: 'A reviewer rejected where this recipient delivers, so deliveries skip it.',
};

// A resource that waits while no request of it is pending and its last request was not rejected (replaced by a newer
// save, say): it is submitted again by saving it (a migration job, by creating it again).
const NO_REQUEST_REMEDY: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'No change request waits: delete and recreate the job to submit it for approval.',
  [ChangeApprovalKind.CustomSqlRules]: 'No change request waits: save the rules again to submit them for approval.',
  [ChangeApprovalKind.RecipientDestination]: 'No change request waits: save the recipient again to submit it for approval.',
};

/** The resource's last change was rejected and no newer request waits. */
export function isRejected(state: AwaitingApprovalState): boolean {
  return state.requestId == null && state.lastDecision?.status === ApprovalStatus.Rejected;
}

/**
 * Whether the caller may open a change request of this kind: an Approver (an Admin holds the permission) every one;
 * anyone else those of the migration jobs and data contracts they read (the server filters them by project), never a
 * recipient's.
 */
export function useCanOpenChange(kind: ChangeApprovalKind): boolean {
  const canReview = useAccess().hasPermission('Approver');

  return canReview || kind !== ChangeApprovalKind.RecipientDestination;
}

/** The reviewer's comment on a decision, quoted, with when it was made. */
export function DecisionComment({ decision }: { decision: ChangeApprovalOutcome }) {
  return (
    <>
      {decision.decidedAt && <span> Decided {formatDateTime(decision.decidedAt)}.</span>}
      {decision.comment && <span> Reviewer comment: “{decision.comment}”</span>}
    </>
  );
}

/**
 * Marks a migration job, a Custom SQL rule or a recipient whose change waits for a second person's approval, or whose
 * change was rejected. Whoever may open the request (see {@link useCanOpenChange}) opens it from here.
 */
export function AwaitingApprovalPill({ kind, requestId, lastDecision }: AwaitingApprovalState & { kind: ChangeApprovalKind }) {
  const canOpen = useCanOpenChange(kind);
  const rejected = isRejected({ requestId, lastDecision });
  const pill = rejected ? (
    <Pill tone="crit" dot title={`${REJECTED_REMEDY[kind]}${lastDecision?.comment ? `: “${lastDecision.comment}”` : ''}`}>
      Rejected
    </Pill>
  ) : (
    <Pill tone="warn" dot title={requestId != null ? "This change waits for a second person's approval" : NO_REQUEST_REMEDY[kind]}>
      Awaiting approval
    </Pill>
  );
  const linked = rejected ? lastDecision?.requestId : requestId;

  if (linked != null && canOpen) {
    return (
      <Link
        to={`/approvals/changes/${linked}`}
        onClick={e => e.stopPropagation()}
        aria-label={rejected ? 'Open the rejected change' : 'Review the pending change'}
      >
        {pill}
      </Link>
    );
  }

  return pill;
}

/** A rejected change's remedy and the reviewer's comment, in a line under the resource's name. */
export function RejectedChangeNote({ kind, requestId, lastDecision }: AwaitingApprovalState & { kind: ChangeApprovalKind }) {
  if (!isRejected({ requestId, lastDecision }) || !lastDecision) {
    return null;
  }

  return (
    <span className="text-xs text-text-muted">
      {REJECTED_REMEDY[kind]}
      {lastDecision.comment ? `: “${lastDecision.comment}”` : ''}
    </span>
  );
}

/**
 * A banner saying what waits for approval, or that its change was rejected and what to do about it (with the reviewer's
 * comment), linking whoever may open the request to it.
 */
export function AwaitingApprovalBanner({
  kind,
  title,
  sub,
  requestId,
  lastDecision,
}: AwaitingApprovalState & {
  kind: ChangeApprovalKind;
  title: string;
  sub: ReactNode;
}) {
  const canReview = useAccess().hasPermission('Approver');
  const canOpen = useCanOpenChange(kind);
  const rejected = isRejected({ requestId, lastDecision });
  const linked = rejected ? lastDecision?.requestId : requestId;
  const action = linked != null && canOpen ? (
    <Link to={`/approvals/changes/${linked}`}>
      <Button size="sm">{!rejected && canReview ? 'Review the change' : 'View the change'}</Button>
    </Link>
  ) : undefined;

  if (rejected && lastDecision) {
    return (
      <Banner
        tone="crit"
        icon={<XCircle />}
        title={REJECTED_REMEDY[kind]}
        sub={
          <>
            {REJECTED_EFFECT[kind]}
            <DecisionComment decision={lastDecision} />
          </>
        }
        actions={action}
      />
    );
  }

  return (
    <Banner
      tone="warn"
      icon={<Hourglass />}
      title={title}
      sub={
        <>
          {sub}
          {requestId == null && <span> {NO_REQUEST_REMEDY[kind]}</span>}
        </>
      }
      actions={action}
    />
  );
}

/** What a create answered when the server submitted it for approval. */
export interface SubmittedForApproval {
  awaitingApproval?: boolean;
  message?: string | null;
}

/**
 * After a create: when the server submitted the new resource for approval, says so and what happens next (its message,
 * such as "Submitted for approval: the job does not run … until a second person approves it").
 */
export function noteSubmittedForApproval(result: SubmittedForApproval) {
  if (!result.awaitingApproval) {
    return;
  }

  toast.info(result.message ?? 'Submitted for approval: it takes effect once a second person approves it.');
}
