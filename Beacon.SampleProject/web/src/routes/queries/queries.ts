import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { ApiError, fetchJson } from '@/lib/api';
import { beaconApi } from '@/api/client';

// ---------- Versions (existing) ----------

export function useQueryVersionsQuery(queryId: number | undefined) {
  return useQuery({
    queryKey: ['queries', queryId, 'versions'],
    queryFn: () => beaconApi().getQueryVersions(queryId as number),
    enabled: typeof queryId === 'number' && Number.isFinite(queryId),
  });
}

// ---------- Query detail ----------

export interface QueryStepParameter {
  name: string;
  type: number;
  description: string | null;
  placeholder: string | null;
}

export interface QueryStep {
  stepId: number;
  stepOrder: number;
  name: string;
  description: string | null;
  sqlValue: string;
  dataSourceId: number;
  dataSourceName: string;
  dataSourceType: number;
  databaseEngineType: number | null;
  databaseEngineDescription: string;
  parameters: QueryStepParameter[];
}

export interface QuerySubscriptionListItem {
  subscriptionId: number;
  createdTime: string;
  name: string;
  subscribers: string;
  cronExpression: string;
}

export interface NotificationStatisticsEntry {
  date: string;
  totalExecutions: number;
  successfulNotifications: number;
  failedExecutions: number;
  successRate: number;
}

export interface ExecutionTimeDataPoint {
  date: string;
  avgExecutionTimeMs: number;
  minExecutionTimeMs: number;
  maxExecutionTimeMs: number;
}

/**
 * Wire-aligned with `Beacon.Core.Services.QueryDetailsData`. Computed
 * properties on the C# side (`isMultiStep`, `isCrossDataSource`,
 * `dataSourceNames`, etc.) are serialized as plain readonly fields.
 */
export interface QueryDetail {
  id: number;
  name: string;
  description: string | null;
  createdTime: string;
  totalExecutions: number;
  sentNotifications: number;
  steps: QueryStep[];
  finalQuery: string | null;
  finalQueryDataSourceId: number | null;
  aiActorId: number | null;
  aiActorName: string | null;
  isLocked: boolean;
  subscriptions: QuerySubscriptionListItem[];
  notificationHistory: NotificationStatisticsEntry[];
  avgExecutionTimeMs: number;
  minExecutionTimeMs: number;
  maxExecutionTimeMs: number;
  executionTimeHistory: ExecutionTimeDataPoint[];
  isMultiStep: boolean;
  isCrossDataSource: boolean;
  isCrossDatabase: boolean;
  dataSourceNames: string[];
}

export function useQueryDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['query', id],
    queryFn: () => fetchJson<QueryDetail>(`/beacon/api/queries/${id}`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

// ---------- Lock toggle ----------

export interface ToggleQueryLockResult {
  queryId: number;
  isLocked: boolean;
  changedAt: string;
  changedBy: string | null;
}

function describeError(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    return err.body || `${fallback} (${err.status})`;
  }
  if (err instanceof Error) {
    return err.message;
  }
  return fallback;
}

export function useToggleQueryLock(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { lock: boolean; userId?: string | null }) =>
      fetchJson<ToggleQueryLockResult>(`/beacon/api/queries/${id}/lock`, {
        method: 'POST',
        body: JSON.stringify({ lock: vars.lock, userId: vars.userId ?? null }),
      }),
    onSuccess: result => {
      qc.invalidateQueries({ queryKey: ['query', id] });
      qc.invalidateQueries({ queryKey: ['queries'] });
      toast.success(result.isLocked ? 'Query locked' : 'Query unlocked');
    },
    onError: (err: unknown) => {
      toast.error(describeError(err, 'Lock toggle failed'));
    },
  });
}

// ---------- Change history ----------

export interface QueryChangeHistoryEntry {
  id: number;
  queryStepId: number;
  queryStepName: string | null;
  queryStepOrder: number;
  aiActorId: number | null;
  aiActorName: string | null;
  aiActorExecutionId: number | null;
  aiActorPlanId: number | null;
  userId: string | null;
  previousSql: string;
  newSql: string;
  changeReason: string | null;
  changeSource: number;
  changedAt: string;
}

export interface QueryChangeHistoryResult {
  queryId: number;
  changes: QueryChangeHistoryEntry[];
}

interface ChangeHistoryFilters {
  stepId?: number;
  changeSource?: number;
  fromDate?: string;
  toDate?: string;
  maxResults?: number;
}

export function useQueryChangeHistoryQuery(
  id: number | undefined,
  filters: ChangeHistoryFilters = {},
) {
  return useQuery({
    queryKey: ['query', id, 'change-history', filters],
    queryFn: () => {
      const params = new URLSearchParams();
      if (filters.stepId != null) params.set('stepId', String(filters.stepId));
      if (filters.changeSource != null) params.set('changeSource', String(filters.changeSource));
      if (filters.fromDate) params.set('fromDate', filters.fromDate);
      if (filters.toDate) params.set('toDate', filters.toDate);
      if (filters.maxResults != null) params.set('maxResults', String(filters.maxResults));
      const qs = params.toString();
      return fetchJson<QueryChangeHistoryResult>(
        `/beacon/api/queries/${id}/change-history${qs ? `?${qs}` : ''}`,
      );
    },
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

/**
 * Wire-aligned with `Beacon.Core.Data.Enums.ChangeSource`.
 *  1 = User, 2 = AiActor, 3 = Import.
 */
export const CHANGE_SOURCE_LABEL: Record<number, string> = {
  1: 'user',
  2: 'AI actor',
  3: 'import',
};
