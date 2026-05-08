import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { ApiError } from '@/lib/api';
import { useRecipientsQuery } from '@/routes/recipients/queries';
import { useCreateSubscription } from './queries';

const SCHEMA = z.object({
  queryId: z.number({ message: 'Query id is required' }).int().min(1, 'Query id is required'),
  cronExpression: z.string().trim().min(1, 'Cron expression is required').max(200),
  recipientIds: z.array(z.number().int()).min(1, 'Pick at least one recipient'),
  maxRows: z.number().int().min(0).nullable().optional(),
  timeoutSeconds: z.number().int().min(0).nullable().optional(),
  includeAttachment: z.boolean(),
  showQuery: z.boolean(),
  storeResults: z.boolean(),
  createTasks: z.boolean(),
});

type FormValues = z.infer<typeof SCHEMA>;

interface AddSubscriptionDialogProps {
  open: boolean;
  onClose: () => void;
}

export function AddSubscriptionDialog({ open, onClose }: AddSubscriptionDialogProps) {
  const { data: recipientsData, isLoading: recipientsLoading } = useRecipientsQuery();
  const createMutation = useCreateSubscription();

  const {
    register,
    handleSubmit,
    reset,
    watch,
    setValue,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: {
      queryId: 0,
      cronExpression: '0 9 * * *',
      recipientIds: [],
      maxRows: null,
      timeoutSeconds: null,
      includeAttachment: false,
      showQuery: false,
      storeResults: false,
      createTasks: false,
    },
  });

  useEffect(() => {
    if (!open) return;
    reset({
      queryId: 0,
      cronExpression: '0 9 * * *',
      recipientIds: [],
      maxRows: null,
      timeoutSeconds: null,
      includeAttachment: false,
      showQuery: false,
      storeResults: false,
      createTasks: false,
    });
  }, [open, reset]);

  const recipients = recipientsData?.entries ?? [];
  const recipientIds = watch('recipientIds');

  const toggleRecipient = (id: number) => {
    const current = new Set(recipientIds);
    if (current.has(id)) {
      current.delete(id);
    } else {
      current.add(id);
    }
    setValue('recipientIds', Array.from(current), { shouldValidate: true });
  };

  const onSubmit = handleSubmit(async values => {
    try {
      await createMutation.mutateAsync({
        queryId: values.queryId,
        cronExpression: values.cronExpression,
        recipientIds: values.recipientIds,
        maxRows: values.maxRows ?? null,
        timeoutSeconds: values.timeoutSeconds ?? null,
        includeAttachment: values.includeAttachment,
        showQuery: values.showQuery,
        storeResults: values.storeResults,
        createTasks: values.createTasks,
      });
      toast.success('Subscription created');
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New subscription"
      sub="Schedule a query and route its results to recipients."
      size="md"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="subscription-form"
            className="btn btn--primary"
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Saving…' : 'Create subscription'}
          </button>
        </>
      }
    >
      <form id="subscription-form" onSubmit={onSubmit} noValidate>
        <div className="q-field">
          <label className="q-label" htmlFor="sub-query-id">
            Query id<span className="q-label__req">*</span>
          </label>
          <input
            id="sub-query-id"
            type="number"
            className={`q-input${errors.queryId ? ' q-input--error' : ''}`}
            {...register('queryId', { valueAsNumber: true })}
          />
          <div className="q-help">Numeric id of the query to schedule. Multi-step picker ships in a later batch.</div>
          {errors.queryId && <div className="q-error">{errors.queryId.message}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="sub-cron">
            Cron expression<span className="q-label__req">*</span>
          </label>
          <input
            id="sub-cron"
            type="text"
            className={`q-input mono${errors.cronExpression ? ' q-input--error' : ''}`}
            placeholder="0 9 * * *"
            {...register('cronExpression')}
          />
          <div className="q-help">Standard 5-field cron. Example: <span className="mono">0 9 * * *</span> runs daily at 09:00.</div>
          {errors.cronExpression && <div className="q-error">{errors.cronExpression.message}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label">
            Recipients<span className="q-label__req">*</span>
          </label>
          {recipientsLoading && <div className="muted">Loading recipients…</div>}
          {!recipientsLoading && recipients.length === 0 && (
            <div className="muted">No recipients yet. Add one from the Recipients page first.</div>
          )}
          {!recipientsLoading && recipients.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6, maxHeight: 180, overflow: 'auto' }}>
              {recipients.map(r => {
                const checked = recipientIds.includes(r.id);
                return (
                  <label key={r.id} style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer' }}>
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={() => toggleRecipient(r.id)}
                    />
                    <span>{r.name}</span>
                    <span className="muted mono" style={{ fontSize: 12 }}>{r.destination}</span>
                  </label>
                );
              })}
            </div>
          )}
          {errors.recipientIds && <div className="q-error">{errors.recipientIds.message as string}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="sub-max-rows">Max rows</label>
          <input
            id="sub-max-rows"
            type="number"
            className="q-input"
            placeholder="No limit"
            {...register('maxRows', { setValueAs: v => v === '' || v == null ? null : Number(v) })}
          />
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="sub-timeout">Timeout (seconds)</label>
          <input
            id="sub-timeout"
            type="number"
            className="q-input"
            placeholder="Default"
            {...register('timeoutSeconds', { setValueAs: v => v === '' || v == null ? null : Number(v) })}
          />
        </div>

        <div className="q-field" style={{ marginTop: 14, display: 'grid', gap: 6 }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" {...register('includeAttachment')} />
            <span>Include results as attachment</span>
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" {...register('showQuery')} />
            <span>Show query text in notification</span>
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" {...register('storeResults')} />
            <span>Store result rows for later viewing</span>
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" {...register('createTasks')} />
            <span>Create tasks for each result row</span>
          </label>
        </div>
      </form>
    </Dialog>
  );
}
