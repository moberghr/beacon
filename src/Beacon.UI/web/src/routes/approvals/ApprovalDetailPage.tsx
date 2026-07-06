import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { toast } from 'sonner';
import {
  AlertTriangle,
  ArrowLeftRight,
  Check,
  Database,
  Layers,
} from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import {
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
import { ApprovalStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  useApprovalDetailQuery,
  useApproveQueryChange,
  useRejectQueryChange,
} from './queries';

const STATUS_LABEL: Record<ApprovalStatus, string> = {
  [ApprovalStatus.Pending]: 'Pending',
  [ApprovalStatus.Approved]: 'Approved',
  [ApprovalStatus.Rejected]: 'Rejected',
};

const STATUS_TONE: Record<ApprovalStatus, PillProps['tone']> = {
  [ApprovalStatus.Pending]: 'neutral',
  [ApprovalStatus.Approved]: 'ok',
  [ApprovalStatus.Rejected]: 'crit',
};

export default function ApprovalDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;

  const detail = useApprovalDetailQuery(Number.isFinite(numericId) ? numericId : undefined);
  const approve = useApproveQueryChange();
  const reject = useRejectQueryChange();
  const [comment, setComment] = useState('');

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
        await approve.mutateAsync({ id: numericId, comment: trimmed });
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
                  {data.queryId
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

          {data.proposedVersion?.finalQuery && (
            <Card>
              <CardHead>
                <Database className="size-3.5 text-text-muted" />
                <CardTitle>Proposed SQL</CardTitle>
              </CardHead>
              <CardBody>
                <pre className="bg-surface-2 border border-border rounded-sm p-3 text-xs m-0 whitespace-pre-wrap overflow-auto max-h-[400px] mono">
                  {data.proposedVersion.finalQuery}
                </pre>
              </CardBody>
            </Card>
          )}

          {data.currentActiveVersion?.finalQuery && data.currentActiveVersion.finalQuery !== data.proposedVersion?.finalQuery && (
            <Card>
              <CardHead>
                <Database className="size-3.5 text-text-muted" />
                <CardTitle>Current active SQL</CardTitle>
              </CardHead>
              <CardBody>
                <pre className="bg-surface-2 border border-border rounded-sm p-3 text-xs m-0 whitespace-pre-wrap overflow-auto max-h-[400px] mono">
                  {data.currentActiveVersion.finalQuery}
                </pre>
              </CardBody>
            </Card>
          )}

          {isPending && (
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
                    disabled={busy}
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
