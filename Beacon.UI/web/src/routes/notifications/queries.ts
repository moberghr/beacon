import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useNotificationsQuery() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => beaconApi().getNotifications(0, 100, undefined, undefined),
  });
}

// Notification detail. Hand-typed wrapper until codegen runs.
export interface NotificationDetailEntry {
  id: number;
  queryId: number;
  queryName: string;
  subscriptionId: number;
  recipientName: string;
  type: number;
  status: number;
  createdTime: string;
  sentAt: string;
  executionTimeMs: number;
  resultCount: number | null;
  results: string | null;
}

interface GetNotificationDetailResult {
  entry: NotificationDetailEntry | null;
}

export function useNotificationDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['notifications', id],
    queryFn: async () =>
      (await beaconApi().getNotificationDetail(id as number)) as unknown as GetNotificationDetailResult,
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}
