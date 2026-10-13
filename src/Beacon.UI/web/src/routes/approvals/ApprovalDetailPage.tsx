import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { toast } from 'sonner';
import {
  AlertTriangle,
  ArrowLeftRight,
  Check,
  Database,
  History,
  Info,
  Layers,
} from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import {
  Banner,
  Button,
  Card,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  Field,
  PageHeader,
  Pill,
  type PillProps,
  Textarea,
} from '@/components/beacon';
import { useAccess } from '@/auth/useAuth';
import { ApprovalStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  useApprovalDetailQuery,
  useApproveQueryChange,
  useRejectQueryChange,
} from './queries';
import { ApprovalChangeDiff } from './ApprovalChangeDiff';

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

export default function ApprovalDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;

  const detail = useApprovalDetailQuery(Number.isFinite(numericId) ? numericId : undefined);
  const approve = useApproveQueryChange();
  const reject = useRejectQueryChange();
  const [comment, setComment] = useState('');
  // Approving and rejecting is an Approver's (an Admin holds it); the query itself needs read permission to open.
  const access = useAccess();
  const canReview = access.hasPermission('Approver');

  if (Number.isNaN(numericId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Workflow" emphasis="Approval" />
        <EmptyState icon={<AlertTriangle />} title="Invalid approval id" description={String(id)} />
      </div>
    );
  }

  const handleAction = async (action: 'approve' | 'reject') => {
    try {
      const trimmed = comment.trim() || null;
      if (action === 'approve') {
        // Bound to what this page shows: the server refuses (409) when the proposal holds other content.
        await approve.mutateAsync({ id: numericId, comment: trimmed, expectedContentHash: data?.contentHash ?? null });
        toast.success('Approval granted');
      } else {
        await reject.mutateAsync({ id: numericId, comment: trimmed });
        toast.success('Approval rejected');
      }
      navigate('/approvals');
    } catch {
      // useApprove/RejectQueryChange (createSimpleMutation) already toast the error.
    }
  };

  const data = detail.data;
  const isPending = data?.status === ApprovalStatus.Pending;
  const busy = approve.isPending || reject.isPending;
  const mayApprove = isPending && data?.mayApprove === true;

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Workflow"
        emphasis={data?.queryName ? `Approval: ${data.queryName}` : 'Approval'}
        sub={
          <span className="text-text-muted">
            <Link to="/approvals" className="text-text-muted hover:text-text">Approvals</Link>
            <span className="mx-1.5">/</span>
            #{numericId}
          </span>
        }
        actions={
          <Link to="/approvals">
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
                <dt className="text-text-muted">Query</dt>
                <dd>
                  {data.queryId && access.canRead
                    ? <Link to={`/queries/${data.queryId}`} className="font-semibold">{data.queryName}</Link>
                    : <span className="font-semibold">{data.queryName}</span>}
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
                <dd className="mono">{data.createdTime ? formatDateTime(data.createdTime) : '—'}</dd>
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
              title="A newer submission replaced this request"
              sub="It can no longer be decided. The newer request for this query is in the approvals list."
            />
          )}

          {isPending && data.isStale && (
            <Banner
              tone="warn"
              icon={<AlertTriangle />}
              title="The query changed since this request was made"
              sub="Its active version is no longer the one this change was made on, so it cannot be approved: reject it, or submit the change again."
            />
          )}

          {isPending && canReview && !mayApprove && !data.isStale && data.approveBlockedReason && (
            <Banner tone="info" icon={<Info />} title="You cannot approve this request" sub={data.approveBlockedReason} />
          )}

          <Card>
            <CardHead>
              <Database className="size-3.5 text-text-muted" />
              <CardTitle>Proposed change</CardTitle>
              <CardSub>
                {data.currentActiveVersion
                  ? `v${data.currentActiveVersion.versionNumber} → v${data.proposedVersion.versionNumber}`
                  : `v${data.proposedVersion.versionNumber}, the query's first version`}
              </CardSub>
            </CardHead>
            <CardBody>
              <ApprovalChangeDiff detail={data} />
            </CardBody>
          </Card>

          {isPending && canReview && (
            <Card>
              <CardHead>
                <Check className="size-3.5 text-text-muted" />
                <CardTitle>Decision</CardTitle>
              </CardHead>
              <CardBody>
                <Field label="Reviewer comment (optional)">
                  <Textarea
                    id="ad-comment"
                    rows={3}
                    placeholder="Optional note shown alongside your decision."
                    value={comment}
                    onChange={e => setComment(e.target.value)}
                  />
                </Field>
                <div className="flex justify-end gap-2.5 mt-3">
                  <Button
                    variant="danger"
                    onClick={() => handleAction('reject')}
                    disabled={busy}
                  >
                    {reject.isPending ? 'Rejecting…' : 'Reject'}
                  </Button>
                  <Button
                    variant="primary"
                    onClick={() => handleAction('approve')}
                    disabled={busy || !mayApprove}
                    title={mayApprove ? undefined : data.approveBlockedReason ?? undefined}
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
