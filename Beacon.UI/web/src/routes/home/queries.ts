import { useQuery } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Using fetchJson directly as codegen requires a running app with the new endpoints.
// Once codegen runs, these can be migrated to beaconApi() calls.

export interface HomePerfBucket {
  label: string;
  avgMs: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
}

export interface GetHomeTrendsResult {
  totalSubscriptions: number;
  subscriptionDelta: number;
  queryExecutions30d: number;
  queryExecutionsDelta: number;
  notificationsSent30d: number;
  notificationsDelta: number;
  anomaliesOpen: number;
  anomaliesAcknowledged: number;
  anomaliesDelta: number;
  avgExecutionMs: number;
  avgExecutionDeltaPct: number;
  fastestQueryName: string | null;
  fastestQueryMs: number;
  fastestQueryDeltaMs: number;
  slowestQueryName: string | null;
  slowestQueryMs: number;
  slowestQueryDeltaMs: number;
  dataSourcesOnline: number;
  recipientsCount: number;
  integrationsCount: number;
  subscriptionsSpark: number[];
  queriesSpark: number[];
  notificationsSpark: number[];
  anomaliesSpark: number[];
  queryTrend30d: number[];
  notificationsTrend30d: number[];
  perfBuckets: HomePerfBucket[];
}

export interface HomeActivityItem {
  tone: string;
  icon: string;
  title: string;
  meta: string | null;
  timestamp: string;
}

export interface GetHomeActivityResult {
  items: HomeActivityItem[];
}

export function useHomeTrendsQuery(days: number) {
  return useQuery({
    queryKey: ['home', 'trends', days],
    queryFn: () => fetchJson<GetHomeTrendsResult>(`/beacon/api/home/trends?days=${days}`),
  });
}

export function useHomeActivityQuery() {
  return useQuery({
    queryKey: ['home', 'activity'],
    queryFn: () => fetchJson<GetHomeActivityResult>('/beacon/api/home/activity?limit=8'),
  });
}

export interface GetHomeMigrationSummaryResult {
  total: number;
  successful: number;
  executions: number;
  errored: number;
}

export function useHomeMigrationSummaryQuery() {
  return useQuery({
    queryKey: ['home', 'migration-summary'],
    queryFn: () => fetchJson<GetHomeMigrationSummaryResult>('/beacon/api/home/migration-summary'),
    retry: false,
  });
}

export interface GetHomeTaskSummaryResult {
  total: number;
  open: number;
  resolved: number;
}

export function useHomeTaskSummaryQuery() {
  return useQuery({
    queryKey: ['home', 'task-summary'],
    queryFn: () => fetchJson<GetHomeTaskSummaryResult>('/beacon/api/home/task-summary'),
    retry: false,
  });
}
