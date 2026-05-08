import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { fetchJson } from '@/lib/api';

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
    queryFn: () => fetchJson<GetNotificationDetailResult>(`/beacon/api/notifications/${id}`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}
