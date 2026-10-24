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

/**
 * What to do about a rejected change: a migration job cannot be edited, so it is deleted and created again; a data
 * source's connection is replaced (the approved one back, or a new one for approval); the LLM connection stays as it was.
 */
export const REJECTED_REMEDY: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'Rejected — delete and recreate the job',
  [ChangeApprovalKind.CustomSqlRules]: 'Rejected — change it and save again',
  [ChangeApprovalKind.RecipientDestination]: 'Rejected — change it and save again',
  [ChangeApprovalKind.DataSourceConnection]: 'Rejected — replace the connection, or delete the data source',
  [ChangeApprovalKind.LlmConnection]: 'Rejected — the LLM connection stays as it was',
  [ChangeApprovalKind.AdminRoleGrant]: 'Rejected — the user does not hold the Admin role',
};

const REJECTED_EFFECT: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'A reviewer rejected this job’s definition, so it never runs as it is.',
  [ChangeApprovalKind.CustomSqlRules]: 'A reviewer rejected the change of these Custom SQL rules, so they are not evaluated as they are.',
  [ChangeApprovalKind.RecipientDestination]: 'A reviewer rejected where this recipient delivers, so deliveries skip it.',
  [ChangeApprovalKind.DataSourceConnection]:
    'A reviewer rejected this connection, so Beacon makes no connection with the data source until it holds an approved one. Replace it with the connection approved before (that applies at once) or with a new one (that waits for approval), or delete the data source.',
  [ChangeApprovalKind.LlmConnection]:
    'A reviewer rejected this change of the LLM connection, so the stored connection stays in use. Save the settings again to submit another change.',
  [ChangeApprovalKind.AdminRoleGrant]:
    'A reviewer rejected this grant of the Admin role, so the user does not hold it. Set the user’s roles again to submit another grant.',
};

// A resource that waits while no request of it is pending and its last request was not rejected (replaced by a newer
// save, say): it is submitted again by saving it (a migration job, by creating it again).
const NO_REQUEST_REMEDY: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'No change request waits: delete and recreate the job to submit it for approval.',
  [ChangeApprovalKind.CustomSqlRules]: 'No change request waits: save the rules again to submit them for approval.',
  [ChangeApprovalKind.RecipientDestination]: 'No change request waits: save the recipient again to submit it for approval.',
  [ChangeApprovalKind.DataSourceConnection]: 'No change request waits: replace the connection to submit it for approval.',
  [ChangeApprovalKind.LlmConnection]: 'No change request waits: save the LLM settings again to submit the change for approval.',
  [ChangeApprovalKind.AdminRoleGrant]: 'No change request waits: set the user’s roles again to submit the grant for approval.',
};

// What a pending change means for its resource, where the generic "waits for approval" says too little.
const PENDING_HINT: Partial<Record<ChangeApprovalKind, string>> = {
  [ChangeApprovalKind.DataSourceConnection]:
    "This connection waits for a second person's approval: Beacon makes no connection with the data source until it is approved.",
};

/** The resource's last change was rejected and no newer request waits. */
export function isRejected(state: AwaitingApprovalState): boolean {
  return state.requestId == null && state.lastDecision?.status === ApprovalStatus.Rejected;
}

/**
 * Whether the caller may open a change request of this kind: an LLM connection's only an Admin now; a recipient's and a
 * data source connection's only an Approver (an Admin holds the permission); the migration jobs' and data contracts' an
 * Approver every one, anyone else those they read (the server filters them by project).
 */
export function useCanOpenChange(kind: ChangeApprovalKind): boolean {
  const access = useAccess();

  switch (kind) {
    case ChangeApprovalKind.LlmConnection:
    case ChangeApprovalKind.AdminRoleGrant:
      return access.isAdmin;
    case ChangeApprovalKind.RecipientDestination:
    case ChangeApprovalKind.DataSourceConnection:
      return access.hasPermission('Approver');
    default:
      return true;
  }
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
    <Pill
      tone="warn"
      dot
      title={requestId != null ? PENDING_HINT[kind] ?? "This change waits for a second person's approval" : NO_REQUEST_REMEDY[kind]}
    >
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

/** What a held data source means, and how to withdraw it. */
export const DATA_SOURCE_HELD_TITLE = 'Held for approval';
export const DATA_SOURCE_HELD_HINT =
  'This data source connects to nothing until a second person approves its connection: no query, preview, subscription run or MCP call reaches it. To withdraw it, delete the data source, or save the connection that was approved before back (that applies at once).';

/** A held data source with no pending request (rejected, withdrawn or replaced): how it is submitted again. */
export const DATA_SOURCE_HELD_NO_REQUEST_TITLE = 'Held: no change request waits';
export const DATA_SOURCE_HELD_NO_REQUEST_HINT =
  'This data source was never approved and connects to nothing, and no request waits for approval (a rejected one leaves none). Save the connection to submit it for approval again.';

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
