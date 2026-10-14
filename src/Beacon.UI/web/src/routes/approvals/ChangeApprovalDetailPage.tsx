import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, ArrowLeftRight, Check, Database, EyeOff, History, Info, Layers } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import {
  Banner,
  Button,
  Card,
  CardBody,
  CardHead,
  CardTitle,
  Field,
  PageHeader,
  Pill,
  type PillProps,
  Textarea,
} from '@/components/beacon';
import { useAccess } from '@/auth/useAuth';
import { ApprovalStatus, ChangeApprovalKind } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  CHANGE_KIND_LABEL,
  useApproveChange,
  useChangeApprovalDetailQuery,
  useRejectChange,
  type ChangeApprovalDetail,
} from './queries';
import { ChangeApprovalDiff } from './ChangeApprovalDiff';

const STATUS_LABEL: Record<ApprovalStatus, string> = {
  [ApprovalStatus.Pending]: 'Pending',
  [ApprovalStatus.Approved]: 'Approved',
  [ApprovalStatus.Rejected]: 'Rejected',
  [ApprovalStatus.Superseded]: 'Superseded',
};

const STATUS_TONE: Record<ApprovalStatus, PillProps['tone']> = {
  [ApprovalStatus.Pending]: 'neutral',
  [ApprovalStatus.Approved]: 'ok',
  [ApprovalStatus.Rejected]: 'crit',
  [ApprovalStatus.Superseded]: 'neutral',
};

function resourceLink(detail: ChangeApprovalDetail): string | null {
  if (!detail.resourceExists) {
    return null;
  }

  switch (detail.kind) {
    case ChangeApprovalKind.MigrationJob:
      return `/migration-jobs/${detail.resourceId}`;
    case ChangeApprovalKind.CustomSqlRules:
      return `/data-quality/${detail.resourceId}`;
    case ChangeApprovalKind.RecipientDestination:
      return '/recipients';
    default:
      return null;
  }
}

export default function ChangeApprovalDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;

  const detail = useChangeApprovalDetailQuery(Number.isFinite(numericId) ? numericId : undefined);
  const approve = useApproveChange();
  const reject = useRejectChange();
  const [comment, setComment] = useState('');
  // Deciding a change is an Approver's (an Admin holds it); anyone else may read the requests of the migration jobs and
  // data contracts they read, never a recipient's (the server answers not found).
  const canReview = useAccess().hasPermission('Approver');

  if (Number.isNaN(numericId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Workflow" emphasis="Change approval" />
        <EmptyState icon={<AlertTriangle />} title="Invalid approval id" description={String(id)} />
      </div>
    );
  }

  const data = detail.data;
  const isPending = data?.status === ApprovalStatus.Pending;
  const mayApprove = isPending && data?.mayApprove === true;
  // The change lies in a part the mask hides: the server refuses an approval without a comment, and so does this page.
  const commentRequired = isPending && data?.commentRequiredToApprove === true;
  const missingComment = commentRequired && comment.trim() === '';
  const busy = approve.isPending || reject.isPending;
  const link = data ? resourceLink(data) : null;

  const handleAction = async (action: 'approve' | 'reject') => {
    try {
      const trimmed = comment.trim() || null;
      if (action === 'approve') {
        // Bound to what this page shows: the server refuses (409) when the request holds another payload.
        await approve.mutateAsync({ id: numericId, comment: trimmed, expectedPayloadHash: data?.payloadHash ?? null });
        toast.success('Change approved');
      } else {
        await reject.mutateAsync({ id: numericId, comment: trimmed });
        toast.success('Change rejected');
      }
      navigate(`/approvals?kind=${data?.kind ?? ''}`);
    } catch {
      // useApproveChange / useRejectChange (createSimpleMutation) already toast the error.
    }
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Workflow"
        emphasis={data ? `Approval: ${data.resourceName}` : 'Change approval'}
        sub={
          <span className="text-text-muted">
            <Link to="/approvals" className="text-text-muted hover:text-text">Approvals</Link>
            <span className="mx-1.5">/</span>
            {data ? `${CHANGE_KIND_LABEL[data.kind] ?? 'Change'} #${numericId}` : `#${numericId}`}
          </span>
        }
        actions={
          <Link to={data ? `/approvals?kind=${data.kind}` : '/approvals'}>
            <Button icon={<ArrowLeftRight />}>All approvals</Button>
          </Link>
        }
      />

      {detail.isLoading && (
        <Card>
          <CardBody><span className="text-text-muted">Loading approval…</span></CardBody>
        </Card>
      )}

      {detail.isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load approval"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      )}

      {data && (
        <>
          <Card>
            <CardHead>
              <Layers className="size-3.5 text-text-muted" />
              <CardTitle>Request</CardTitle>
            </CardHead>
            <CardBody>
              <dl className="grid grid-cols-[180px_1fr] gap-y-2 gap-x-3 m-0">
                <dt className="text-text-muted">{CHANGE_KIND_LABEL[data.kind] ?? 'Resource'}</dt>
                <dd>
                  {link
                    ? <Link to={link} className="font-semibold">{data.resourceName}</Link>
                    : <span className="font-semibold">{data.resourceName}</span>}
                  {!data.resourceExists && <span className="text-text-muted ml-2">(deleted)</span>}
                </dd>
                <dt className="text-text-muted">Status</dt>
                <dd>
                  <Pill tone={STATUS_TONE[data.status] ?? 'neutral'}>
                    {STATUS_LABEL[data.status] ?? `status ${data.status}`}
                  </Pill>
                </dd>
                <dt className="text-text-muted">Submitted by</dt>
                <dd>{data.requestedByUserName ?? <span className="text-text-muted">Unknown</span>}</dd>
                <dt className="text-text-muted">Submitted at</dt>
                <dd className="mono">{formatDateTime(data.createdTime)}</dd>
                {data.reviewedByUserName && (
                  <>
                    <dt className="text-text-muted">Reviewed by</dt>
                    <dd>{data.reviewedByUserName}</dd>
                  </>
                )}
                {data.reviewedAt && (
                  <>
                    <dt className="text-text-muted">Reviewed at</dt>
                    <dd className="mono">{formatDateTime(data.reviewedAt)}</dd>
                  </>
                )}
                {data.reviewComment && (
                  <>
                    <dt className="text-text-muted">Reviewer comment</dt>
                    <dd>{data.reviewComment}</dd>
                  </>
                )}
                <dt className="text-text-muted">Change summary</dt>
                <dd>{data.changeSummary ?? <span className="text-text-muted">—</span>}</dd>
              </dl>
            </CardBody>
          </Card>

          {data.status === ApprovalStatus.Superseded && (
            <Banner
              tone="info"
              icon={<History />}
              title="A newer change replaced this request"
              sub="It can no longer be decided. The newer request, if one waits, is in the approvals list."
            />
          )}

          {isPending && data.isStale && (
            <Banner
              tone="warn"
              icon={<AlertTriangle />}
              title={data.resourceExists ? 'This changed again since the request was made' : 'What this request changes was deleted'}
              sub={data.approveBlockedReason ?? 'It cannot be approved: reject it, or submit the change again.'}
            />
          )}

          {isPending && canReview && !mayApprove && !data.isStale && data.approveBlockedReason && (
            <Banner tone="info" icon={<Info />} title="You cannot approve this request" sub={data.approveBlockedReason} />
          )}

          {isPending && !canReview && (
            <Banner
              tone="info"
              icon={<Info />}
              title="An Approver decides this change"
              sub="You can follow it here: once it is decided, the decision and the reviewer's comment show on this page and on the changed resource's page."
            />
          )}

          {data.changeHiddenByMask && (
            <Banner
              tone="warn"
              icon={<EyeOff />}
              title="The change is in a part the mask hides"
              sub={
                commentRequired
                  ? 'The type, the destination as shown and the header names are the same before and after, but the fingerprints differ: a token, a signature, a query value or a header value changed. Compare the fingerprints with the requester out of band, and say in your comment how you checked the change: approving it requires a comment.'
                  : 'The type, the destination as shown and the header names are the same before and after, but the fingerprints differ: a token, a signature, a query value or a header value changed.'
              }
            />
          )}

          <Card>
            <CardHead>
              <Database className="size-3.5 text-text-muted" />
              <CardTitle>Proposed change</CardTitle>
            </CardHead>
            <CardBody>
              <ChangeApprovalDiff detail={data} />
            </CardBody>
          </Card>

          {isPending && canReview && (
            <Card>
              <CardHead>
                <Check className="size-3.5 text-text-muted" />
                <CardTitle>Decision</CardTitle>
              </CardHead>
              <CardBody>
                <Field
                  htmlFor="cad-comment"
                  label={commentRequired ? 'Reviewer comment (required to approve)' : 'Reviewer comment (optional)'}
                >
                  <Textarea
                    id="cad-comment"
                    rows={3}
                    placeholder={
                      commentRequired
                        ? 'How you checked the change, for example: compared the fingerprints with the requester.'
                        : 'Optional note shown alongside your decision.'
                    }
                    value={comment}
                    onChange={e => setComment(e.target.value)}
                  />
                </Field>
                {missingComment && mayApprove && (
                  <div className="text-xs text-text-muted mt-1.5">
                    Approving this change requires a comment: what changed is hidden by the mask.
                  </div>
                )}
                <div className="flex justify-end gap-2.5 mt-3">
                  <Button variant="danger" onClick={() => handleAction('reject')} disabled={busy}>
                    {reject.isPending ? 'Rejecting…' : 'Reject'}
                  </Button>
                  <Button
                    variant="primary"
                    onClick={() => handleAction('approve')}
                    disabled={busy || !mayApprove || missingComment}
                    title={
                      !mayApprove
                        ? data.approveBlockedReason ?? undefined
                        : missingComment
                          ? 'Say in a comment how you checked the change: it lies in a part the mask hides'
                          : undefined
                    }
                  >
                    {approve.isPending ? 'Approving…' : 'Approve'}
                  </Button>
                </div>
              </CardBody>
            </Card>
          )}
        </>
      )}
    </div>
  );
}
