import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { NotificationType } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const NOTIFICATION_TYPE_LABEL: Record<number, string> = {
  [NotificationType.Teams]: 'Teams',
  [NotificationType.Email]: 'Email',
  [NotificationType.Jira]: 'Jira',
  [NotificationType.Slack]: 'Slack',
  [NotificationType.Webhook]: 'Webhook',
};

export interface RecipientEntry {
  id: number;
  name: string;
  description: string | null;
  destination: string;
  notificationType: number;
  headersJson: string | null;
  bodyTemplate: string | null;
  subscriptionCount: number;
}

interface GetRecipientsResult {
  entries: RecipientEntry[];
}

export interface RecipientFormValues {
  name: string;
  description: string | null;
  destination: string;
  notificationType: NotificationType;
  headersJson: string | null;
  bodyTemplate: string | null;
}

const RECIPIENTS_KEY = ['recipients'] as const;

export function useRecipientsQuery() {
  return useQuery({
    queryKey: RECIPIENTS_KEY,
    queryFn: async () =>
      unwrap<GetRecipientsResult>(await beaconApi().getRecipients()),
  });
}

export function useCreateRecipient() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<RecipientFormValues, { id: number }>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createRecipient(values as never);
        return { id: r.id ?? 0 };
      },
      invalidate: [RECIPIENTS_KEY],
      errorFallback: 'Create recipient failed',
    }),
  );
}

export function useUpdateRecipient() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; values: RecipientFormValues }, void>({
      qc,
      mutationFn: ({ id, values }) => beaconApi().updateRecipient(id, values as never),
      invalidate: [RECIPIENTS_KEY],
      errorFallback: 'Update recipient failed',
    }),
  );
}

export function useDeleteRecipient() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().deleteRecipient(id),
      invalidate: [RECIPIENTS_KEY],
      errorFallback: 'Delete recipient failed',
    }),
  );
}
