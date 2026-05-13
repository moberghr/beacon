import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { fetchJson } from '@/lib/api';

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
    queryFn: async () =>
      (await beaconApi().getHomeTrends(days)) as unknown as GetHomeTrendsResult,
  });
}

export function useHomeActivityQuery() {
  return useQuery({
    queryKey: ['home', 'activity'],
    queryFn: async () =>
      (await beaconApi().getHomeActivity(8)) as unknown as GetHomeActivityResult,
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
    queryFn: async () =>
      (await beaconApi().getHomeMigrationSummary()) as unknown as GetHomeMigrationSummaryResult,
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
    queryFn: async () =>
      (await beaconApi().getHomeTaskSummary()) as unknown as GetHomeTaskSummaryResult,
    retry: false,
  });
}

export type ExecutionUptimeTick = 'ok' | 'warn' | 'crit' | 'muted';

export interface GetExecutionUptimeResult {
  ticks: ExecutionUptimeTick[];
}

export function useExecutionUptimeQuery(hours: number = 24) {
  return useQuery({
    queryKey: ['home', 'uptime', hours],
    queryFn: () => fetchJson<GetExecutionUptimeResult>(`/beacon/api/home/uptime?hours=${hours}`),
    refetchInterval: 60_000, // current hour is partial — refresh once a minute
    retry: false,
  });
}
