import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { fetchPagedList } from '@/lib/paging';
import { usePagedList } from '@/lib/usePagedList';
import {
  AnomalyDetectionMethod,
  AnomalySensitivity,
  FileType,
  NotificationStatus,
  NotificationTrigger,
} from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export interface SubscriptionEntry {
  id: number;
  queryId: number;
  queryName: string;
  cronExpression: string;
  recipientCount: number;
  recipientNames: string[];
  aiActorId: number | null;
  aiActorName: string | null;
  createTasks: boolean;
  storeResults: boolean;
}

export interface CreateSubscriptionPayload {
  queryId: number;
  cronExpression: string;
  recipientIds: number[];
  maxRows: number | null;
  timeoutSeconds: number | null;
  includeAttachment: boolean;
  showQuery: boolean;
  storeResults: boolean;
  createTasks: boolean;
  notificationTrigger: NotificationTrigger;
  minimumRowCount: number | null;
  resultAttachmentType: FileType | null;
  parameters: { queryPlaceholder: string; value: string }[];
  anomalyConfig: {
    detectionMethod: AnomalyDetectionMethod;
    sensitivity: AnomalySensitivity;
    lookbackDays: number;
    minimumDataPoints: number;
    alertOnIncrease: boolean;
    alertOnDecrease: boolean;
  } | null;
}

const SUBSCRIPTIONS_KEY = ['subscriptions'] as const;

export type SubscriptionListStatus = 'active' | 'archived';

/** The subscriptions grid: server-paged, alphabetical by query name, name search + status tab in the URL. */
export function useSubscriptionsList() {
  return usePagedList<SubscriptionEntry, { search: string; status: SubscriptionListStatus }>({
    queryKey: SUBSCRIPTIONS_KEY,
    path: '/beacon/api/subscriptions',
    filters: { search: '', status: 'active' },
    mapFilters: x => ({ search: x.search, archived: x.status === 'archived' }),
    defaultSort: { column: 'queryName', direction: 'asc' },
  });
}

export function useCreateSubscription() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateSubscriptionPayload, { success: boolean; message: string | null }>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createSubscription(values as never);
        return {
          success: r.success ?? false,
          message: r.message ?? null,
        };
      },
      invalidate: [SUBSCRIPTIONS_KEY],
      errorFallback: 'Create subscription failed',
    }),
  );
}

export function useDeleteSubscription() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().deleteSubscription(id),
      invalidate: [SUBSCRIPTIONS_KEY],
      errorFallback: 'Delete subscription failed',
    }),
  );
}

// ---------- Subscription detail ----------

/**
 * Wire-aligned with `Beacon.Core.Handlers.Subscriptions.SubscriptionDetail`.
 * Enum fields (`notificationTrigger`, `resultAttachmentType`, anomaly enums)
 * serialize as numeric ids — see the generated enums re-exported from
 * `@/lib/enums`.
 */
export interface SubscriptionDetailRecipient {
  id: number;
  name: string;
  description: string | null;
  /** Always null: destinations are secrets, shown masked to admins on the recipients page only. */
  destination: string | null;
  notificationType: number;
}

export interface SubscriptionDetailParameter {
  queryPlaceholder: string | null;
  value: string | null;
}

export interface SubscriptionDetailAnomalyConfig {
  enabled: boolean;
  detectionMethod: number;
  sensitivity: number;
  lookbackDays: number;
  alertOnIncrease: boolean;
  alertOnDecrease: boolean;
  minimumDataPoints: number;
}

/**
 * Backend emits this as a computed string (`ArchivedTime.HasValue ? "Archived" : "Active"`
 * in `SubscriptionService`) — there is no backend enum to import for it, so this
 * union + constant is the single source of truth for the two allowed values on the
 * frontend. Compare against `SubscriptionStatus.Active` instead of a bare string literal.
 */
export const SubscriptionStatus = {
  Active: 'Active',
  Archived: 'Archived',
} as const;
export type SubscriptionStatus = (typeof SubscriptionStatus)[keyof typeof SubscriptionStatus];

export interface SubscriptionDetail {
  id: number;
  queryId: number;
  queryName: string;
  status: SubscriptionStatus;
  cronExpression: string;
  cronDescription: string;
  cronNextAt: string | null;
  aiActorId: number | null;
  aiActorName: string | null;
  maxRows: number | null;
  minimumRowCount: number | null;
  includeAttachment: boolean;
  resultAttachmentType: number | null;
  showQuery: boolean;
  timeoutSeconds: number | null;
  storeResults: boolean;
  createTasks: boolean;
  notificationTrigger: number;
  parameters: SubscriptionDetailParameter[];
  recipients: SubscriptionDetailRecipient[];
  anomalyConfig: SubscriptionDetailAnomalyConfig | null;
}

interface GetSubscriptionDetailResult {
  detail: SubscriptionDetail;
}

const subscriptionDetailKey = (id: number) => ['subscriptions', id, 'detail'] as const;

export function useSubscriptionDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['subscriptions', id, 'detail'] as const,
    queryFn: async () =>
      unwrap<GetSubscriptionDetailResult>(await beaconApi().getSubscriptionDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

// ---------- Execution history (subscription-scoped) ----------

export interface SubscriptionExecutionEntry {
  id: number;
  subscriptionId: number;
  queryName: string;
  status: number;
  resultCount: number;
  executionTimeMs: number;
  createdTime: string;
  aiActorId: number | null;
  aiActorName: string | null;
  comment: string | null;
  recipientNames: string[];
}

/**
 * The latest executions as a bounded sample for the KPI tiles and right rail (success rate, last failure).
 * Not a grid — the Execution history tab pages through `useSubscriptionExecutionsList`.
 */
export function useSubscriptionExecutionsQuery(id: number | undefined, sampleSize = 200) {
  return useQuery({
    queryKey: ['subscriptions', id, 'executions', 'sample', sampleSize] as const,
    queryFn: () =>
      fetchPagedList<SubscriptionExecutionEntry>('/beacon/api/notifications', {
        subscriptionId: id,
        page: 0,
        pageSize: sampleSize,
        sort: '-createdTime',
      }),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

/** The Execution history tab: server-paged, newest first; URL keys prefixed `runs` (`?runsPage=2`). */
export function useSubscriptionExecutionsList(id: number) {
  return usePagedList<SubscriptionExecutionEntry>({
    queryKey: ['subscriptions', id, 'executions'],
    path: '/beacon/api/notifications',
    fixedParams: { subscriptionId: id },
    defaultSort: { column: 'createdTime', direction: 'desc' },
    urlPrefix: 'runs',
  });
}

// ---------- Anomaly chart ----------

export interface AnomalyChartPoint {
  dateTime: string;
  resultCount: number;
  isAnomaly: boolean;
  notificationSent: boolean;
  anomalySeverity: string | null;
  queryExecutionHistoryId: number | null;
}

export interface AnomalyChartResult {
  hasAnomalyDetection: boolean;
  points: AnomalyChartPoint[];
  baselineMean: number | null;
  upperThreshold: number | null;
  lowerThreshold: number | null;
}

export function useSubscriptionAnomalyChart(id: number | undefined, days = 30) {
  return useQuery({
    queryKey: ['subscriptions', id, 'anomaly-chart', days] as const,
    queryFn: async () =>
      unwrap<AnomalyChartResult>(await beaconApi().getSubscriptionAnomalyChart(id as number, days)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

// ---------- Mutations ----------

const subscriptionMutationKeys = (id: number | undefined) =>
  typeof id === 'number'
    ? [SUBSCRIPTIONS_KEY, subscriptionDetailKey(id)]
    : [SUBSCRIPTIONS_KEY];

export function useTestSubscription(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, void>({
      qc,
      mutationFn: () => beaconApi().testSubscription(id as number),
      invalidate:
        typeof id === 'number'
          ? [
              subscriptionDetailKey(id),
              ['subscriptions', id, 'executions'],
              ['subscriptions', id, 'anomaly-chart'],
            ]
          : [],
      successMsg: 'Subscription executed',
      errorFallback: 'Test run failed',
    }),
  );
}

export function useArchiveSubscription(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, void>({
      qc,
      mutationFn: () => beaconApi().deleteSubscription(id as number),
      invalidate: subscriptionMutationKeys(id),
      successMsg: 'Subscription archived',
      errorFallback: 'Archive failed',
    }),
  );
}

export function useReactivateSubscription(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, void>({
      qc,
      mutationFn: () => beaconApi().reactivateSubscription(id as number),
      invalidate: subscriptionMutationKeys(id),
      successMsg: 'Subscription reactivated',
      errorFallback: 'Reactivate failed',
    }),
  );
}

export function useAddSubscriptionRecipients(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number[], void>({
      qc,
      mutationFn: (recipientIds) =>
        beaconApi().addSubscriptionRecipients(id as number, { recipientIds }),
      invalidate: subscriptionMutationKeys(id),
      successMsg: 'Recipients added',
      errorFallback: 'Add recipient failed',
    }),
  );
}

export function useRemoveSubscriptionRecipient(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (recipientId) =>
        beaconApi().removeSubscriptionRecipient(id as number, recipientId),
      invalidate: subscriptionMutationKeys(id),
      successMsg: 'Recipient removed',
      errorFallback: 'Remove recipient failed',
    }),
  );
}

// ---------- Enum maps (mirror Beacon.Core.Data.Enums) ----------

export const NOTIFICATION_TRIGGER_LABEL: Record<number, { label: string; description: string }> = {
  [NotificationTrigger.OnResultCountChange]: { label: 'On change', description: 'When result count changes' },
  [NotificationTrigger.Always]: { label: 'Always', description: 'Every execution' },
  [NotificationTrigger.OnResultCountIncrease]: { label: 'On increase', description: 'When result count increases' },
};

export const NOTIFICATION_STATUS_LABEL: Record<number, string> = {
  [NotificationStatus.Created]: 'Created',
  [NotificationStatus.NotificationSent]: 'Sent',
  [NotificationStatus.NotificationSilenced]: 'Silenced',
  [NotificationStatus.NoResults]: 'No results',
  [NotificationStatus.Timeout]: 'Timeout',
  [NotificationStatus.BelowThreshold]: 'Below threshold',
  [NotificationStatus.Failed]: 'Failed',
};

export const FILE_TYPE_LABEL: Record<number, string> = {
  [FileType.Csv]: 'CSV',
  [FileType.Xlsx]: 'XLSX',
};

export const ANOMALY_DETECTION_METHOD_LABEL: Record<number, string> = {
  [AnomalyDetectionMethod.StandardDeviation]: 'Standard deviation',
  [AnomalyDetectionMethod.IQR]: 'IQR',
  [AnomalyDetectionMethod.PercentageChange]: 'Percentage change',
};

export const ANOMALY_SENSITIVITY_LABEL: Record<number, string> = {
  [AnomalySensitivity.Low]: 'Low',
  [AnomalySensitivity.Medium]: 'Medium',
  [AnomalySensitivity.High]: 'High',
};
