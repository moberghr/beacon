import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import {
  fetchControlTowerHealth,
  fetchControlTowerStatistics,
  fetchControlTowerSubscriptionDetail,
  type ControlTowerFilters,
} from './api';

const REFETCH_INTERVAL_MS = 30_000;

export function useControlTowerQuery(filters: ControlTowerFilters) {
  return useQuery({
    queryKey: ['control-tower', filters],
    queryFn: async () => {
      const [stats, health] = await Promise.all([
        fetchControlTowerStatistics(filters),
        fetchControlTowerHealth(filters),
      ]);
      return { stats, entries: health.entries, totalCount: health.totalCount };
    },
    refetchInterval: REFETCH_INTERVAL_MS,
    refetchOnWindowFocus: true,
  });
}

export function useControlTowerSubscriptionDetail(
  subscriptionId: number | null,
  timeRangeDays: number,
) {
  return useQuery({
    queryKey: ['control-tower', 'subscription-detail', subscriptionId, timeRangeDays],
    queryFn: () =>
      fetchControlTowerSubscriptionDetail(subscriptionId as number, timeRangeDays),
    enabled: subscriptionId != null,
  });
}

export function useQueryFolders() {
  return useQuery({
    queryKey: ['query-folders'],
    queryFn: async () => {
      const response = await beaconApi().getQueryFolders();
      return response.folders ?? [];
    },
    staleTime: 60_000,
  });
}

/**
 * Live updates for Control Tower now flow through the shared
 * `useHubInvalidations` hook mounted in AppShell — it invalidates the
 * `['control-tower']` key on both `JobStatusChanged` and
 * `NotificationCreated`. This stub stays as a no-op so existing call
 * sites don't break; remove once the page imports are cleaned up.
 */
export function useControlTowerLiveUpdates(): void {
  // intentionally empty — see useHubInvalidations.ts
}
