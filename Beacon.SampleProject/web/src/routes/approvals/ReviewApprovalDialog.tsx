import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
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

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Review approval"
      sub="Inspect the proposed change before approving or rejecting."
      size="lg"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting || approve.isPending || reject.isPending}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => handleAction('reject')}
            disabled={isSubmitting || approve.isPending || reject.isPending || data == null}
          >
            {reject.isPending ? 'Rejecting…' : 'Reject'}
          </button>
          <button
            type="submit"
            form="review-approval-form"
            className="btn btn--primary"
            disabled={isSubmitting || approve.isPending || reject.isPending || data == null}
          >
            {approve.isPending ? 'Approving…' : 'Approve'}
          </button>
        </>
      }
    >
      <form id="review-approval-form" onSubmit={onSubmit} noValidate>
        {detail.isLoading && <div className="muted">Loading approval detail…</div>}
        {detail.isError && (
          <div className="q-error">
            {detail.error instanceof Error ? detail.error.message : 'Failed to load approval'}
          </div>
        )}
        {data && (
          <>
            <div className="kv" style={{ marginBottom: 16 }}>
              <div className="kv__row">
                <div className="kv__label">Query</div>
                <div className="kv__value" style={{ fontWeight: 600 }}>{data.queryName}</div>
              </div>
              <div className="kv__row">
                <div className="kv__label">Submitted by</div>
                <div className="kv__value">{data.requestedByUserName ?? <span className="muted">Unknown</span>}</div>
              </div>
              <div className="kv__row">
                <div className="kv__label">Submitted at</div>
                <div className="kv__value">{data.createdTime ? formatDateTime(data.createdTime as unknown as string) : '—'}</div>
              </div>
              <div className="kv__row">
                <div className="kv__label">Change summary</div>
                <div className="kv__value">{data.changeSummary ?? <span className="muted">—</span>}</div>
              </div>
            </div>

            {proposedSql && (
              <div className="modal__section">
                <div className="modal__section-head">
                  <div className="modal__section-title">Proposed SQL</div>
                </div>
                <pre style={{
                  background: 'var(--surface-2)',
                  border: '1px solid var(--border)',
                  borderRadius: 'var(--r-sm)',
                  padding: 12,
                  fontSize: 12,
                  margin: 0,
                  whiteSpace: 'pre-wrap',
                  overflow: 'auto',
                  maxHeight: 320,
                }}>{proposedSql}</pre>
              </div>
            )}

            <div className="q-field" style={{ marginTop: 16 }}>
              <label className="q-label" htmlFor="approval-comment">Reviewer comment</label>
              <textarea
                id="approval-comment"
                className={`q-textarea${errors.comment ? ' q-textarea--error' : ''}`}
                rows={3}
                placeholder="Optional note shown alongside your decision."
                {...register('comment')}
              />
              {errors.comment && <div className="q-error">{errors.comment.message}</div>}
            </div>
          </>
        )}
      </form>
    </Dialog>
  );
}
