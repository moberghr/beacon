import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input, Select, Textarea } from '@/components/beacon';
import { describeError } from '@/lib/api';
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
            toast.error(describeError(err, 'Request failed'));
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
          <Button type="button" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" form="recipient-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save changes' : 'Create recipient'}
          </Button>
        </>
      }
    >
      <form id="recipient-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3.5">
        <Field label={<>Notification type <span className="text-crit">*</span></>}>
          <Select
            id="recipient-type"
            aria-invalid={!!errors.notificationType}
            {...register('notificationType', { valueAsNumber: true })}
          >
            {NOTIFICATION_TYPE_ENTRIES.map(([id, label]) => (
              <option key={id} value={id}>{label}</option>
            ))}
          </Select>
          {errors.notificationType && <span className="text-xs text-crit">{errors.notificationType.message}</span>}
        </Field>

        <Field label={<>Name <span className="text-crit">*</span></>}>
          <Input
            id="recipient-name"
            type="text"
            autoComplete="off"
            aria-invalid={!!errors.name}
            {...register('name')}
          />
          {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
        </Field>

        <Field
          label={<>{destinationLabel(Number(notificationType))} <span className="text-crit">*</span></>}
          hint={destinationHint(Number(notificationType))}
        >
          <Input
            id="recipient-destination"
            type="text"
            autoComplete="off"
            aria-invalid={!!errors.destination}
            {...register('destination')}
          />
          {errors.destination && <span className="text-xs text-crit">{errors.destination.message}</span>}
        </Field>

        <Field label="Description">
          <Input
            id="recipient-description"
            type="text"
            placeholder="Optional"
            {...register('description')}
          />
        </Field>

        {isWebhook && (
          <>
            <Field label="Custom headers (JSON)" hint="Optional JSON object of HTTP headers to send with the webhook.">
              <Textarea
                id="recipient-headers"
                rows={3}
                placeholder='{"Authorization": "Bearer ..."}'
                {...register('headersJson')}
              />
            </Field>
            <Field
              label="Body template"
              hint={<>Placeholders: {`{{Timestamp}} {{SubscriptionName}} {{TotalRecords}} {{Records}}`}.</>}
            >
              <Textarea
                id="recipient-body"
                rows={6}
                placeholder="Optional — leave empty for default payload"
                {...register('bodyTemplate')}
              />
            </Field>
          </>
        )}
      </form>
    </Dialog>
  );
}
