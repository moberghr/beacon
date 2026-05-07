import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 3 — endpoints are not yet in the generated NSwag client
// (codegen requires a running backend). These thin wrappers match the shape
// of `Beacon.Core.Handlers.Recipients.*Handler` exactly. Once `npm run
// codegen` is run against /openapi/v1.json, swap to `beaconApi().getRecipients()`
// etc. and delete this file's HTTP code.

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
    queryFn: () => fetchJson<GetRecipientsResult>('/beacon/api/recipients'),
  });
}

export function useCreateRecipient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (values: RecipientFormValues) =>
      fetchJson<{ id: number }>('/beacon/api/recipients', {
        method: 'POST',
        body: JSON.stringify(values),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: RECIPIENTS_KEY }),
  });
}

export function useUpdateRecipient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, values }: { id: number; values: RecipientFormValues }) =>
      fetchJson<void>(`/beacon/api/recipients/${id}`, {
        method: 'PUT',
        body: JSON.stringify(values),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: RECIPIENTS_KEY }),
  });
}

export function useDeleteRecipient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      fetchJson<void>(`/beacon/api/recipients/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: RECIPIENTS_KEY }),
  });
}
