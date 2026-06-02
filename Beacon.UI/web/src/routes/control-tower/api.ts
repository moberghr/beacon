/**
 * Hand-rolled Control Tower API wrappers. The generated NSwag client is regenerated
 * from a running server; until the next `npm run codegen` we drive the new query
 * parameters (timeRangeDays, sortBy) and the subscription-detail endpoint directly.
 *
 * Types match the C# DTOs in Beacon.Core/Models/ControlTower.
 */

export enum HealthStatus {
  Green = 1,
  Amber = 2,
  Red = 3,
  Stalled = 4,
}

export enum NotificationStatus {
  Created = 1,
  NotificationSent = 2,
  NotificationSilenced = 3,
  NoResults = 4,
  Timeout = 5,
  BelowThreshold = 6,
  Failed = 7,
}

export enum ControlTowerSortBy {
  WorstFirst = 0,
  Name = 1,
  SuccessRate = 2,
  Executions = 3,
  OpenTasks = 4,
  Anomalies = 5,
  LastExecution = 6,
}

export interface AnomalySparklinePoint {
  date: string;
  anomalyCount: number;
}

export interface ControlTowerSubscriptionHealthData {
  subscriptionId: number;
  queryName: string;
  dataSourceName: string | null;
  folderPath: string | null;
  healthStatus: HealthStatus;
  totalExecutions: number;
  successfulExecutions: number;
  failedExecutions: number;
  successRate: number;
  lastExecutionTime: string | null;
  lastExecutionStatus: NotificationStatus | null;
  lastResultCount: number | null;
  unresolvedTaskCount: number;
  totalTaskCount: number;
  anomalyCount30Days: number;
  anomalySparkline: AnomalySparklinePoint[];
  isActive: boolean;
  createTasks: boolean;
  storeResults: boolean;
  hasAnomalyDetection: boolean;
  aiActorId: number | null;
  aiActorName: string | null;
}

export interface ControlTowerStatistics {
  totalSubscriptions: number;
  healthySubscriptions: number;
  warningSubscriptions: number;
  criticalSubscriptions: number;
  stalledSubscriptions: number;
  totalUnresolvedTasks: number;
  totalAnomalies30Days: number;
  overallSuccessRate: number;
  timeRangeDays: number;
}

export interface ControlTowerExecutionItem {
  executionId: number;
  createdTime: string;
  notificationStatus: NotificationStatus;
  resultCount: number;
  executionTimeMs: number;
  errorMessage: string | null;
}

export interface ControlTowerOpenTask {
  taskId: number;
  createdTime: string;
  snoozedUntil: string | null;
  latestResultCount: number;
  priority: number;
  assigneeUserId: string | null;
}

export interface ControlTowerAnomaly {
  anomalyId: number;
  detectedTime: string;
  severity: string;
  currentValue: number;
  explanation: string | null;
  acknowledged: boolean;
}

export interface ControlTowerSubscriptionDetail {
  subscriptionId: number;
  queryName: string;
  queryId: number;
  folderPath: string | null;
  cronExpression: string;
  timeRangeDays: number;
  recentExecutions: ControlTowerExecutionItem[];
  openTasks: ControlTowerOpenTask[];
  recentAnomalies: ControlTowerAnomaly[];
}

export interface ControlTowerFilters {
  searchKeyword?: string;
  folderId?: number;
  healthStatus?: HealthStatus;
  hasUnresolvedTasks?: boolean;
  timeRangeDays: number;
  sortBy: ControlTowerSortBy;
}

const BASE = '/beacon/api/control-tower';

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue;
    search.set(key, String(value));
  }
  const str = search.toString();
  return str ? `?${str}` : '';
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

function filterParams(filters: ControlTowerFilters): Record<string, string | number | boolean | undefined> {
  return {
    searchKeyword: filters.searchKeyword,
    folderId: filters.folderId,
    healthStatus: filters.healthStatus,
    hasUnresolvedTasks: filters.hasUnresolvedTasks,
    timeRangeDays: filters.timeRangeDays,
  };
}

export async function fetchControlTowerStatistics(
  filters: ControlTowerFilters,
): Promise<ControlTowerStatistics> {
  const result = await getJson<{ statistics: ControlTowerStatistics }>(
    `${BASE}/statistics${buildQuery(filterParams(filters))}`,
  );
  return result.statistics;
}

export async function fetchControlTowerHealth(
  filters: ControlTowerFilters,
  page = 0,
  pageSize = 200,
): Promise<{ entries: ControlTowerSubscriptionHealthData[]; totalCount: number }> {
  return getJson(
    `${BASE}/health${buildQuery({
      page,
      pageSize,
      ...filterParams(filters),
      sortBy: filters.sortBy,
    })}`,
  );
}

export async function fetchControlTowerSubscriptionDetail(
  subscriptionId: number,
  timeRangeDays: number,
): Promise<ControlTowerSubscriptionDetail> {
  const result = await getJson<{ detail: ControlTowerSubscriptionDetail }>(
    `${BASE}/subscriptions/${subscriptionId}/detail${buildQuery({ timeRangeDays })}`,
  );
  return result.detail;
}
