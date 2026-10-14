import { useEffect, useRef } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { AlertTriangle, History, Info } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { Banner, Button, Field, Textarea } from '@/components/beacon';
import { ApprovalStatus } from '@/lib/enums';
import { formatDateTime } from '@/lib/format';
import {
  useApprovalDetailQuery,
  useApproveQueryChange,
  useRejectQueryChange,
} from './queries';
import { ApprovalChangeDiff } from './ApprovalChangeDiff';

const SCHEMA = z.object({
  comment: z.string().trim().max(2000, 'Max 2000 characters').optional(),
});

type FormValues = z.infer<typeof SCHEMA>;

interface ReviewApprovalDialogProps {
  open: boolean;
  approvalId: number | null;
  onClose: () => void;
}

export function ReviewApprovalDialog({ open, approvalId, onClose }: ReviewApprovalDialogProps) {
  const detail = useApprovalDetailQuery(approvalId ?? undefined);
  const approve = useApproveQueryChange();
  const reject = useRejectQueryChange();

  // Both footer buttons submit the form so the comment always passes RHF/zod
  // validation (max 2000 chars); the clicked button records the intent here.
  const actionRef = useRef<'approve' | 'reject'>('approve');

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { comment: '' },
  });

  useEffect(() => {
    if (open) reset({ comment: '' });
  }, [open, approvalId, reset]);

  const onSubmit = handleSubmit(async values => {
    if (approvalId == null) return;
    const comment = values.comment?.trim() || null;
    try {
      if (actionRef.current === 'approve') {
        // Bound to what the dialog shows: the server refuses (409) when the proposal holds other content.
        await approve.mutateAsync({ id: approvalId, comment, expectedContentHash: data?.contentHash ?? null });
        toast.success('Approval granted');
      } else {
        await reject.mutateAsync({ id: approvalId, comment });
        toast.success('Approval rejected');
      }
      onClose();
    } catch {
      // useApprove/RejectQueryChange (createSimpleMutation) already toast the error.
    }
  });

  const data = detail.data;
  const isPending = data?.status === ApprovalStatus.Pending;
  const mayApprove = isPending && data?.mayApprove === true;
  const busy = isSubmitting || approve.isPending || reject.isPending;

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Review approval"
      sub="Inspect the proposed change before approving or rejecting."
      size="lg"
      footer={
        <>
          <Button onClick={onClose} disabled={busy}>Cancel</Button>
          <Button
            type="submit"
            form="review-approval-form"
            variant="danger"
            onClick={() => { actionRef.current = 'reject'; }}
            disabled={busy || !isPending}
          >
            {reject.isPending ? 'Rejecting…' : 'Reject'}
          </Button>
          <Button
            type="submit"
            form="review-approval-form"
            variant="primary"
            onClick={() => { actionRef.current = 'approve'; }}
            disabled={busy || !mayApprove}
            title={mayApprove ? undefined : data?.approveBlockedReason ?? undefined}
          >
            {approve.isPending ? 'Approving…' : 'Approve'}
          </Button>
        </>
      }
    >
      <form id="review-approval-form" onSubmit={onSubmit} noValidate>
        {detail.isLoading && <div className="text-text-muted">Loading approval detail…</div>}
        {detail.isError && (
          <div className="text-xs text-crit">
            {detail.error instanceof Error ? detail.error.message : 'Failed to load approval'}
          </div>
        )}
        {data && (
          <>
            <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm mb-4">
              <KV label="Query" value={<span className="font-semibold">{data.queryName}</span>} />
              <KV
                label="Submitted by"
                value={data.requestedByUserName ?? <span className="text-text-muted">Unknown</span>}
              />
              <KV
                label="Submitted at"
                value={data.createdTime ? formatDateTime(data.createdTime) : '—'}
              />
              <KV
                label="Change summary"
                value={data.changeSummary ?? <span className="text-text-muted">—</span>}
              />
            </dl>

            <div className="flex flex-col gap-2 mb-3">
              {data.status === ApprovalStatus.Superseded && (
                <Banner
                  tone="info"
                  icon={<History />}
                  title="A newer submission replaced this request"
                  sub="It can no longer be decided."
                />
              )}
              {isPending && data.isStale && (
                <Banner
                  tone="warn"
                  icon={<AlertTriangle />}
                  title="The query changed since this request was made"
                  sub="It cannot be approved: reject it, or submit the change again."
                />
              )}
              {isPending && !mayApprove && !data.isStale && data.approveBlockedReason && (
                <Banner tone="info" icon={<Info />} title="You cannot approve this request" sub={data.approveBlockedReason} />
              )}
            </div>

            <ApprovalChangeDiff detail={data} />

            <div className="mt-4">
              <Field label="Reviewer comment">
                <Textarea
                  id="approval-comment"
                  rows={3}
                  placeholder="Optional note shown alongside your decision."
                  aria-invalid={!!errors.comment}
                  {...register('comment')}
                />
                {errors.comment && <span className="text-xs text-crit">{errors.comment.message}</span>}
              </Field>
            </div>
          </>
        )}
      </form>
    </Dialog>
  );
}

function KV({ label, value }: { label: React.ReactNode; value: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1 border-b border-dashed border-border last:border-b-0">
      <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</dt>
      <dd className="text-sm text-right">{value}</dd>
    </div>
  );
}
