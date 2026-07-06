import { useQuery } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import type { NotificationStatus } from '@/lib/enums';

// Local strict mirror of the generated `NotificationEntry` — dates are
// strings on the wire (see `unwrap` docs in @/lib/api).
export interface NotificationEntry {
  id: number;
  subscriptionId: number;
  queryName: string;
  status: NotificationStatus;
  resultCount: number;
  executionTimeMs: number;
  createdTime: string;
  aiActorId: number | null;
  aiActorName: string | null;
  comment: string | null;
  recipientNames: string[];
}

interface GetNotificationsResult {
  entries: NotificationEntry[];
  totalCount: number;
}

export function useNotificationsQuery() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: async () =>
      unwrap<GetNotificationsResult>(await beaconApi().getNotifications(0, 100, undefined, undefined)),
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
      unwrap<GetNotificationDetailResult>(await beaconApi().getNotificationDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}
