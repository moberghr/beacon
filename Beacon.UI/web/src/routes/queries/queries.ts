import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

// ---------- Versions ----------

export function useQueryVersionsQuery(queryId: number | undefined) {
  return useQuery({
    queryKey: ['queries', queryId, 'versions'],
    queryFn: () => beaconApi().getQueryVersions(queryId as number),
    enabled: typeof queryId === 'number' && Number.isFinite(queryId),
  });
}

export function useQueryVersionDetailQuery(versionId: number | undefined) {
  return useQuery({
    queryKey: ['query-version', versionId],
    queryFn: () => beaconApi().getQueryVersionDetail(versionId as number),
    enabled: typeof versionId === 'number' && Number.isFinite(versionId),
  });
}

export function useQueryVersionDiffQuery(
  versionIdA: number | undefined,
  versionIdB: number | undefined,
) {
  return useQuery({
    queryKey: ['query-version-diff', versionIdA, versionIdB],
    queryFn: () => beaconApi().diffQueryVersions(versionIdA as number, versionIdB as number),
    enabled:
      typeof versionIdA === 'number' &&
      Number.isFinite(versionIdA) &&
      typeof versionIdB === 'number' &&
      Number.isFinite(versionIdB),
  });
}

export function useRestoreQueryVersion(queryId: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (versionId) => beaconApi().restoreQueryVersion(versionId),
      invalidate: [
        ['query', queryId],
        ['queries', queryId, 'versions'],
      ],
      successMsg: 'Version restored',
      errorFallback: 'Restore failed',
    }),
  );
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
    queryFn: async () =>
      unwrap<QueryDetail>(await beaconApi().getQueryDetail(id as number)),
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

export function useToggleQueryLock(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ lock: boolean; userId?: string | null }, ToggleQueryLockResult>({
      qc,
      mutationFn: async (vars) =>
        unwrap<ToggleQueryLockResult>(await beaconApi().toggleQueryLock(id as number, {
          lock: vars.lock,
          userId: vars.userId ?? null,
        })),
      invalidate: [['query', id], ['queries']],
      successMsg: (_vars, result) => (result.isLocked ? 'Query locked' : 'Query unlocked'),
      errorFallback: 'Lock toggle failed',
    }),
  );
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
    queryFn: async () =>
      unwrap<QueryChangeHistoryResult>(await beaconApi().getQueryChangeHistory(
        id as number,
        filters.stepId,
        filters.changeSource,
        filters.fromDate ? new Date(filters.fromDate) : undefined,
        filters.toDate ? new Date(filters.toDate) : undefined,
        filters.maxResults,
      )),
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

// ---------- Editor: parameter type, save, preview ----------
//
// Wire-aligned with `Beacon.Core.Data.Enums.ParameterType` and the new
// `UpdateQuery` / `ExecuteQueryPreview` / `ExecuteStepPreview` handlers
// added in Phase 3 Batch 5f.

export const PARAMETER_TYPE = {
  Number: 1,
  DateTime: 2,
  String: 3,
} as const;
export type ParameterTypeId = typeof PARAMETER_TYPE[keyof typeof PARAMETER_TYPE];

export const PARAMETER_TYPE_LABEL: Record<ParameterTypeId, string> = {
  [PARAMETER_TYPE.Number]: 'Number',
  [PARAMETER_TYPE.DateTime]: 'DateTime',
  [PARAMETER_TYPE.String]: 'String',
};

/**
 * Wire shape sent to PUT /beacon/api/queries/{id}. Mirrors `QueryData` on
 * the backend; only the fields the React editor edits are required, the
 * remainder ride along untouched from the loaded detail.
 */
export interface UpdateQueryStepParameterPayload {
  name: string;
  type: ParameterTypeId;
  description: string | null;
  placeholder: string | null;
}

export interface UpdateQueryStepPayload {
  stepId: number;
  stepOrder: number;
  name: string;
  description: string | null;
  sqlValue: string;
  dataSourceId: number;
  dataSourceName: string;
  dataSourceType: number;
  databaseEngineType: number | null;
  parameters: UpdateQueryStepParameterPayload[];
}

export interface UpdateQueryPayload {
  queryId: number;
  name: string;
  description: string | null;
  steps: UpdateQueryStepPayload[];
  finalQuery: string | null;
  finalQueryDataSourceId: number | null;
}

export interface UpdateQueryResult {
  queryId: number;
  success: boolean;
}

export function useUpdateQueryMutation(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<UpdateQueryPayload, UpdateQueryResult>({
      qc,
      mutationFn: async (payload) =>
        unwrap<UpdateQueryResult>(await beaconApi().updateQuery(id as number, payload as never)),
      invalidate: [['query', id], ['queries', id, 'versions']],
      successMsg: 'Query saved',
      errorFallback: 'Save failed',
    }),
  );
}

// ---------- Preview ----------
//
// Wire-aligned with `Beacon.Core.Models.Queries.QueryStepResult` and
// `QueryExecutionResult`. Rows arrive as `Dictionary<string, object?>` —
// keys are column names, values are JSON primitives or null.

export type PreviewRow = Record<string, unknown>;

export interface QueryStepPreviewResult {
  stepOrder: number;
  stepName: string;
  sqlQuery: string;
  dataSourceName: string;
  databaseEngine: string;
  databaseEngineType: number;
  previewResults: PreviewRow[];
  allResults: PreviewRow[];
  totalRows: number;
  executionTimeMs: number;
  success: boolean;
  errorMessage: string | null;
}

export interface QueryExecutionPreviewResult {
  stepResults: QueryStepPreviewResult[];
  finalResult: {
    success: boolean;
    error: string | null;
    rowCount: number;
    rows: PreviewRow[];
    columns: string[];
    executionTime: string;
  } | null;
  success: boolean;
  errorMessage: string | null;
  totalExecutionTimeMs: number;
  isMultiStep: boolean;
  isCrossDataSource: boolean;
  isCrossDatabase: boolean;
  dataSourcesInvolved: string[];
}

export interface ParameterValueInput {
  name: string;
  value: string;
}

export function usePreviewStepMutation(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ stepOrder: number; parameters?: ParameterValueInput[] }, QueryStepPreviewResult>({
      qc,
      mutationFn: async (vars) =>
        unwrap<QueryStepPreviewResult>(await beaconApi().executeStepPreview(id as number, vars.stepOrder, {
          parameters: (vars.parameters ?? null) as never,
        })),
      errorFallback: 'Step preview failed',
    }),
  );
}

export function usePreviewQueryMutation(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, QueryExecutionPreviewResult>({
      qc,
      mutationFn: async () =>
        unwrap<QueryExecutionPreviewResult>(await beaconApi().executeQueryPreview(id as number)),
      errorFallback: 'Query preview failed',
    }),
  );
}

// ---------- Queries list ----------

export interface QueryListItem {
  queryId: number;
  name: string;
  description: string | null;
  createdTime: string;
  subscriptionsCount: number;
  folderId: number | null;
  folderPath: string | null;
  aiActorId: number | null;
  aiActorName: string | null;
  steps: { stepId: number; stepOrder: number; name: string; dataSourceName: string }[];
}

// Backend wire shape — Beacon.Core.Helpers.PagedList<T>: { items, totalCount }.
export interface PagedQueriesResponse {
  items: QueryListItem[];
  totalCount: number;
}

export interface QueriesListParams {
  searchTerm?: string;
  dataSourceId?: number;
  folderId?: number;
  page?: number;
  pageSize?: number;
}

export function useQueriesListQuery(params: QueriesListParams = {}) {
  return useQuery({
    queryKey: ['queries', 'list', params],
    queryFn: async () =>
      unwrap<PagedQueriesResponse>(await beaconApi().getQueries(
        undefined,
        params.dataSourceId,
        undefined,
        params.folderId,
        params.searchTerm,
        params.page ?? 1,
        params.pageSize ?? 50,
      )),
  });
}

export interface CreateQueryArgs {
  name: string;
  description?: string | null;
}

export function useCreateQuery() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateQueryArgs, { queryId: number }>({
      qc,
      mutationFn: async (args) => {
        const r = await beaconApi().createQuery({
          name: args.name,
          description: args.description ?? null,
        });
        return { queryId: r.queryId ?? 0 };
      },
      invalidate: [['queries']],
      errorFallback: 'Failed to create query',
    }),
  );
}
