/**
 * Control Tower API wrappers. These call the generated NSwag client and assert
 * the loose generated payloads into the strict types below (which mirror the C#
 * DTOs in Beacon.Core/Models/ControlTower and drive the UI's enum/lookup logic).
 *
 * The strict types are kept local rather than imported from the generated client
 * because the generated DTOs make every field optional, which would break the
 * required-field access throughout the Control Tower UI. Enums come from the
 * generated client via '@/lib/enums'.
 */

import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import type { ControlTowerSortBy, HealthStatus, NotificationStatus } from '@/lib/enums';

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

export async function fetchControlTowerStatistics(
  filters: ControlTowerFilters,
): Promise<ControlTowerStatistics> {
  const result = await beaconApi().getControlTowerStatistics(
    undefined,
    filters.folderId,
    filters.healthStatus,
    filters.hasUnresolvedTasks,
    filters.searchKeyword,
    filters.timeRangeDays,
  );
  return unwrap<ControlTowerStatistics>(result.statistics);
}

export async function fetchControlTowerHealth(
  filters: ControlTowerFilters,
  page = 0,
  pageSize = 200,
): Promise<{ entries: ControlTowerSubscriptionHealthData[]; totalCount: number }> {
  const result = await beaconApi().getControlTowerHealth(
    page,
    pageSize,
    undefined,
    filters.folderId,
    filters.healthStatus,
    filters.hasUnresolvedTasks,
    filters.searchKeyword,
    filters.timeRangeDays,
    filters.sortBy,
  );
  return {
    entries: unwrap<ControlTowerSubscriptionHealthData[]>(result.entries),
    totalCount: result.totalCount,
  };
}

export async function fetchControlTowerSubscriptionDetail(
  subscriptionId: number,
  timeRangeDays: number,
): Promise<ControlTowerSubscriptionDetail> {
  const result = await beaconApi().getControlTowerSubscriptionDetail(subscriptionId, timeRangeDays);
  return unwrap<ControlTowerSubscriptionDetail>(result.detail);
}
