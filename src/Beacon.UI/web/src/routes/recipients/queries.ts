import { useMutation, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { NotificationType } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const NOTIFICATION_TYPE_LABEL: Record<number, string> = {
  [NotificationType.Teams]: 'Teams',
  [NotificationType.Email]: 'Email',
  [NotificationType.Jira]: 'Jira',
  [NotificationType.Slack]: 'Slack',
  [NotificationType.Webhook]: 'Webhook',
};

/**
 * `destination`, `headersJson` and `bodyTemplate` are returned to admins only, with secrets masked (`********`);
 * they are null for everyone else. Sending a masked value back on update keeps the stored secret while the destination
 * stays the same. `secretsUnreadable` (admins only) means a stored value cannot be decrypted and must be entered again.
 */
export interface RecipientEntry {
  id: number;
  name: string;
  description: string | null;
  destination: string | null;
  notificationType: number;
  headersJson: string | null;
  bodyTemplate: string | null;
  secretsUnreadable?: boolean;
  subscriptionCount: number;
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

/** The recipients grid: server-paged, alphabetical; search (name, description) in the URL. */
export function useRecipientsList() {
  return usePagedList<RecipientEntry, { search: string }>({
    queryKey: RECIPIENTS_KEY,
    path: '/beacon/api/recipients',
    filters: { search: '' },
    defaultSort: { column: 'name', direction: 'asc' },
  });
}

/** What a recipient picker needs; full `RecipientEntry` rows satisfy it. */
export interface RecipientOption {
  id: number;
  name: string;
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
