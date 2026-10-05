import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import type { HealthStatus } from '@/lib/enums';
import { usePagedList } from '@/lib/usePagedList';
import {
  fetchControlTowerStatistics,
  fetchControlTowerSubscriptionDetail,
  type ControlTowerFilters,
  type ControlTowerSubscriptionHealthData,
} from './api';

const REFETCH_INTERVAL_MS = 30_000;

/** URL-held filter values of the Control Tower grid ('' = not filtered). */
export interface ControlTowerListFilters extends Record<string, string> {
  searchKeyword: string;
  folderId: string;
  healthStatus: string;
  openTasks: string;
  timeRangeDays: string;
}

const FILTER_DEFAULTS: ControlTowerListFilters = {
  searchKeyword: '',
  folderId: '',
  healthStatus: 'all',
  openTasks: '',
  timeRangeDays: '30',
};

export function toControlTowerFilters(filters: ControlTowerListFilters): ControlTowerFilters {
  return {
    searchKeyword: filters.searchKeyword || undefined,
    folderId: filters.folderId === '' ? undefined : Number(filters.folderId),
    healthStatus: filters.healthStatus === 'all' ? undefined : (Number(filters.healthStatus) as HealthStatus),
    hasUnresolvedTasks: filters.openTasks === 'true' ? true : undefined,
    timeRangeDays: Number(filters.timeRangeDays) || 30,
  };
}

/**
 * The subscription health grid. Without a sort the server orders worst first (most open tasks, then
 * lowest success rate); sortable by queryName, successRate, totalExecutions and unresolvedTaskCount.
 */
export function useControlTowerList() {
  return usePagedList<ControlTowerSubscriptionHealthData, ControlTowerListFilters>({
    queryKey: ['control-tower'],
    path: '/beacon/api/control-tower/health',
    filters: FILTER_DEFAULTS,
    mapFilters: x => ({ ...toControlTowerFilters(x) }),
    refetchInterval: REFETCH_INTERVAL_MS,
  });
}

export function useControlTowerStatistics(filters: ControlTowerListFilters) {
  const apiFilters = toControlTowerFilters(filters);
  return useQuery({
    queryKey: ['control-tower', 'statistics', apiFilters],
    queryFn: () => fetchControlTowerStatistics(apiFilters),
    placeholderData: keepPreviousData,
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
