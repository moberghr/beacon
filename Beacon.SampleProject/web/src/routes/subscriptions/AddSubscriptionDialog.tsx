import { useEffect, useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { ApiError } from '@/lib/api';
import { useRecipientsQuery, NOTIFICATION_TYPE_LABEL } from '@/routes/recipients/queries';
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

const DEFAULTS: FormValues = {
  queryId: 0,
  cronExpression: '0 9 * * *',
  recipientIds: [],
  maxRows: null,
  timeoutSeconds: null,
  includeAttachment: false,
  showQuery: false,
  storeResults: false,
  createTasks: false,
};

interface AddSubscriptionDialogProps {
  open: boolean;
  onClose: () => void;
}

export function AddSubscriptionDialog({ open, onClose }: AddSubscriptionDialogProps) {
  const { data: recipientsData, isLoading: recipientsLoading } = useRecipientsQuery();
  const createMutation = useCreateSubscription();

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: DEFAULTS,
    mode: 'onTouched',
  });
  const { register, watch, setValue, reset, formState: { errors } } = form;

  useEffect(() => {
    if (!open) return;
    reset(DEFAULTS);
  }, [open, reset]);

  const recipients = recipientsData?.entries ?? [];
  const recipientIds = watch('recipientIds');
  const queryId = watch('queryId');
  const cronExpression = watch('cronExpression');

  const selectedRecipients = useMemo(
    () => recipients.filter(r => recipientIds.includes(r.id)),
    [recipients, recipientIds],
  );

  const toggleRecipient = (id: number) => {
    const current = new Set(recipientIds);
    if (current.has(id)) {
      current.delete(id);
    } else {
      current.add(id);
    }
    setValue('recipientIds', Array.from(current), { shouldValidate: true });
  };

  const onFinish = async () => {
    const values = form.getValues();
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
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'query',
      title: 'Query',
      description: 'Pick a query and schedule.',
      fields: ['queryId', 'cronExpression'],
      render: () => (
        <>
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
            <div className="q-help">
              Numeric id of the query to schedule. A multi-step query picker ships in a later batch.
            </div>
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
            <div className="q-help">
              Standard 5-field cron. Example: <span className="mono">0 9 * * *</span> runs daily at 09:00.
            </div>
            {errors.cronExpression && <div className="q-error">{errors.cronExpression.message}</div>}
          </div>
        </>
      ),
    },
    {
      id: 'recipients',
      title: 'Recipients',
      description: 'Where alerts are delivered.',
      fields: ['recipientIds'],
      render: () => (
        <>
          <div className="q-field">
            <label className="q-label">
              Recipients<span className="q-label__req">*</span>
            </label>
            {recipientsLoading && <div className="muted">Loading recipients…</div>}
            {!recipientsLoading && recipients.length === 0 && (
              <div className="muted">
                No recipients yet. Add one from the Recipients page first.
              </div>
            )}
            {!recipientsLoading && recipients.length > 0 && (
              <div
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 6,
                  maxHeight: 220,
                  overflow: 'auto',
                }}
              >
                {recipients.map(r => {
                  const checked = recipientIds.includes(r.id);
                  return (
                    <label
                      key={r.id}
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 8,
                        padding: '6px 8px',
                        border: '1px solid var(--border)',
                        borderRadius: 8,
                        cursor: 'pointer',
                      }}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        onChange={() => toggleRecipient(r.id)}
                      />
                      <span style={{ fontWeight: 500 }}>{r.name}</span>
                      <span className="muted mono" style={{ fontSize: 12 }}>
                        {r.destination}
                      </span>
                      <span
                        className="pill pill--neutral mono"
                        style={{ fontSize: 10, marginLeft: 'auto' }}
                      >
                        {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                      </span>
                    </label>
                  );
                })}
              </div>
            )}
            {errors.recipientIds && (
              <div className="q-error">{errors.recipientIds.message as string}</div>
            )}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="sub-max-rows">Max rows</label>
            <input
              id="sub-max-rows"
              type="number"
              className="q-input"
              placeholder="No limit"
              {...register('maxRows', {
                setValueAs: v => (v === '' || v == null ? null : Number(v)),
              })}
            />
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="sub-timeout">Timeout (seconds)</label>
            <input
              id="sub-timeout"
              type="number"
              className="q-input"
              placeholder="Default"
              {...register('timeoutSeconds', {
                setValueAs: v => (v === '' || v == null ? null : Number(v)),
              })}
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
        </>
      ),
    },
    {
      id: 'review',
      title: 'Review',
      description: 'Confirm and create.',
      render: () => {
        const values = form.getValues();
        return (
          <dl className="stepper__review">
            <dt>Query id</dt>
            <dd className="mono">#{queryId}</dd>
            <dt>Schedule</dt>
            <dd className="mono">{cronExpression}</dd>
            <dt>Recipients</dt>
            <dd>
              {selectedRecipients.length === 0
                ? <span className="muted">None selected</span>
                : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                    {selectedRecipients.map(r => (
                      <span key={r.id}>
                        {r.name}{' '}
                        <span className="muted mono" style={{ fontSize: 12 }}>
                          {r.destination}
                        </span>
                      </span>
                    ))}
                  </div>
                )}
            </dd>
            <dt>Max rows</dt>
            <dd>{values.maxRows ?? <span className="muted">No limit</span>}</dd>
            <dt>Timeout</dt>
            <dd>
              {values.timeoutSeconds == null
                ? <span className="muted">Default</span>
                : <span className="mono">{values.timeoutSeconds}s</span>}
            </dd>
            <dt>Options</dt>
            <dd>
              {[
                values.includeAttachment && 'Attachment',
                values.showQuery && 'Show query',
                values.storeResults && 'Store results',
                values.createTasks && 'Create tasks',
              ].filter(Boolean).join(', ') || <span className="muted">None</span>}
            </dd>
          </dl>
        );
      },
    },
  ];

  return (
    <StepperDialog<FormValues>
      open={open}
      onClose={onClose}
      title="New subscription"
      sub="Schedule a query and route its results to recipients."
      size="md"
      steps={steps}
      form={form}
      onFinish={onFinish}
      busy={createMutation.isPending}
      finishLabel="Create subscription"
    />
  );
}
