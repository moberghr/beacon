import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Textarea } from '@/components/beacon';
import { ApiError } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import {
  useApprovalDetailQuery,
  useApproveQueryChange,
  useRejectQueryChange,
} from './queries';

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

  const handleAction = async (action: 'approve' | 'reject') => {
    if (approvalId == null) return;
    const comment = (document.getElementById('approval-comment') as HTMLTextAreaElement | null)?.value.trim() || null;
    try {
      if (action === 'approve') {
        await approve.mutateAsync({ id: approvalId, comment });
        toast.success('Approval granted');
      } else {
        await reject.mutateAsync({ id: approvalId, comment });
        toast.success('Approval rejected');
      }
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Action failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  };

  const onSubmit = handleSubmit(() => handleAction('approve'));

  const data = detail.data;
  const proposedSql = data?.proposedVersion?.finalQuery;
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
            variant="danger"
            onClick={() => handleAction('reject')}
            disabled={busy || data == null}
          >
            {reject.isPending ? 'Rejecting…' : 'Reject'}
          </Button>
          <Button
            type="submit"
            form="review-approval-form"
            variant="primary"
            disabled={busy || data == null}
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
                value={data.createdTime ? formatDateTime(data.createdTime as unknown as string) : '—'}
              />
              <KV
                label="Change summary"
                value={data.changeSummary ?? <span className="text-text-muted">—</span>}
              />
            </dl>

            {proposedSql && (
              <div className="mt-2">
                <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1.5">
                  Proposed SQL
                </div>
                <pre className="bg-surface-2 border border-border rounded-sm p-3 text-xs m-0 whitespace-pre-wrap overflow-auto max-h-80 mono">
                  {proposedSql}
                </pre>
              </div>
            )}

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
