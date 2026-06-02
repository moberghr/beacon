import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

export const NotificationTypeId = {
  Teams: 1,
  Email: 2,
  Jira: 3,
  Slack: 4,
  Webhook: 5,
} as const;
export type NotificationTypeId = (typeof NotificationTypeId)[keyof typeof NotificationTypeId];

export const NOTIFICATION_TYPE_LABEL: Record<number, string> = {
  1: 'Teams',
  2: 'Email',
  3: 'Jira',
  4: 'Slack',
  5: 'Webhook',
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
  notificationType: NotificationTypeId;
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
