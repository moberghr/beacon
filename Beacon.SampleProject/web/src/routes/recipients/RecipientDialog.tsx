import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { ApiError } from '@/lib/api';
import {
  NotificationTypeId,
  NOTIFICATION_TYPE_LABEL,
  useCreateRecipient,
  useUpdateRecipient,
  type RecipientEntry,
} from './queries';

const SCHEMA = z.object({
  name: z.string().trim().min(1, 'Name is required').max(200),
  description: z.string().trim().max(500).optional(),
  destination: z.string().trim().min(1, 'Destination is required').max(2000),
  notificationType: z.number().int().min(1, 'Pick a notification type').max(5, 'Pick a notification type'),
  headersJson: z.string().trim().max(4000).optional(),
  bodyTemplate: z.string().trim().max(8000).optional(),
});

type FormValues = z.infer<typeof SCHEMA>;

interface RecipientDialogProps {
  open: boolean;
  onClose: () => void;
  recipient?: RecipientEntry | null;
}

const NOTIFICATION_TYPE_ENTRIES: Array<[number, string]> =
  Object.entries(NOTIFICATION_TYPE_LABEL).map(([k, v]) => [Number(k), v]);

function destinationLabel(type: number): string {
  switch (type) {
    case NotificationTypeId.Email:
      return 'Email address';
    case NotificationTypeId.Slack:
      return 'Slack webhook URL';
    case NotificationTypeId.Teams:
      return 'Teams webhook URL';
    case NotificationTypeId.Webhook:
      return 'Webhook URL';
    case NotificationTypeId.Jira:
      return 'Jira connection string';
    default:
      return 'Destination';
  }
}

function destinationHint(type: number): string {
  switch (type) {
    case NotificationTypeId.Email:
      return 'Where Beacon will send the alert email.';
    case NotificationTypeId.Slack:
      return 'Slack incoming webhook URL.';
    case NotificationTypeId.Teams:
      return 'Microsoft Teams incoming webhook URL.';
    case NotificationTypeId.Webhook:
      return 'Beacon will POST a JSON payload to this URL.';
    case NotificationTypeId.Jira:
      return 'Format: domain;project-key;email;api-key';
    default:
      return '';
  }
}

export function RecipientDialog({ open, onClose, recipient }: RecipientDialogProps) {
  const isEdit = recipient != null;

  const {
    register,
    handleSubmit,
    reset,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: {
      name: '',
      description: '',
      destination: '',
      notificationType: NotificationTypeId.Email,
      headersJson: '',
      bodyTemplate: '',
    },
  });

  const createMutation = useCreateRecipient();
  const updateMutation = useUpdateRecipient();

  useEffect(() => {
    if (!open) return;
    reset({
      name: recipient?.name ?? '',
      description: recipient?.description ?? '',
      destination: recipient?.destination ?? '',
      notificationType: (recipient?.notificationType as NotificationTypeId) ?? NotificationTypeId.Email,
      headersJson: recipient?.headersJson ?? '',
      bodyTemplate: recipient?.bodyTemplate ?? '',
    });
  }, [open, recipient, reset]);

  const notificationType = watch('notificationType');

  const onSubmit = handleSubmit(async values => {
    const payload = {
      name: values.name,
      description: values.description?.trim() ? values.description.trim() : null,
      destination: values.destination,
      notificationType: values.notificationType as NotificationTypeId,
      headersJson: values.headersJson?.trim() ? values.headersJson.trim() : null,
      bodyTemplate: values.bodyTemplate?.trim() ? values.bodyTemplate.trim() : null,
    };

    try {
      if (isEdit) {
        await updateMutation.mutateAsync({ id: recipient.id, values: payload });
        toast.success(`Updated recipient '${payload.name}'`);
      } else {
        await createMutation.mutateAsync(payload);
        toast.success(`Created recipient '${payload.name}'`);
      }
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  const isWebhook = Number(notificationType) === NotificationTypeId.Webhook;

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isEdit ? 'Edit recipient' : 'Add a recipient'}
      sub="Where Beacon sends alerts — email, Slack, Teams, Jira, or a webhook."
      size="md"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="recipient-form"
            className="btn btn--primary"
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Saving…' : isEdit ? 'Save changes' : 'Create recipient'}
          </button>
        </>
      }
    >
      <form id="recipient-form" onSubmit={onSubmit} noValidate>
        <div className="q-field">
          <label className="q-label" htmlFor="recipient-type">
            Notification type<span className="q-label__req">*</span>
          </label>
          <select
            id="recipient-type"
            className={`q-select${errors.notificationType ? ' q-select--error' : ''}`}
            {...register('notificationType', { valueAsNumber: true })}
          >
            {NOTIFICATION_TYPE_ENTRIES.map(([id, label]) => (
              <option key={id} value={id}>{label}</option>
            ))}
          </select>
          {errors.notificationType && <div className="q-error">{errors.notificationType.message}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="recipient-name">
            Name<span className="q-label__req">*</span>
          </label>
          <input
            id="recipient-name"
            className={`q-input${errors.name ? ' q-input--error' : ''}`}
            type="text"
            autoComplete="off"
            {...register('name')}
          />
          {errors.name && <div className="q-error">{errors.name.message}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="recipient-destination">
            {destinationLabel(Number(notificationType))}<span className="q-label__req">*</span>
          </label>
          <input
            id="recipient-destination"
            className={`q-input${errors.destination ? ' q-input--error' : ''}`}
            type="text"
            autoComplete="off"
            {...register('destination')}
          />
          <div className="q-help">{destinationHint(Number(notificationType))}</div>
          {errors.destination && <div className="q-error">{errors.destination.message}</div>}
        </div>

        <div className="q-field" style={{ marginTop: 14 }}>
          <label className="q-label" htmlFor="recipient-description">Description</label>
          <input
            id="recipient-description"
            className="q-input"
            type="text"
            placeholder="Optional"
            {...register('description')}
          />
        </div>

        {isWebhook && (
          <>
            <div className="q-field" style={{ marginTop: 14 }}>
              <label className="q-label" htmlFor="recipient-headers">Custom headers (JSON)</label>
              <textarea
                id="recipient-headers"
                className="q-textarea"
                rows={3}
                placeholder='{"Authorization": "Bearer ..."}'
                {...register('headersJson')}
              />
              <div className="q-help">Optional JSON object of HTTP headers to send with the webhook.</div>
            </div>
            <div className="q-field" style={{ marginTop: 14 }}>
              <label className="q-label" htmlFor="recipient-body">Body template</label>
              <textarea
                id="recipient-body"
                className="q-textarea"
                rows={6}
                placeholder="Optional — leave empty for default payload"
                {...register('bodyTemplate')}
              />
              <div className="q-help">
                Placeholders: {`{{Timestamp}} {{SubscriptionName}} {{TotalRecords}} {{Records}}`}.
              </div>
            </div>
          </>
        )}
      </form>
    </Dialog>
  );
}
