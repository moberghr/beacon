import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { describeError, fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 4 — endpoints are not yet in the generated NSwag client
// (codegen requires a running backend). Hand-typed wrappers mirror the C# DTOs
// in `Beacon.Core.Handlers.Subscriptions.*Handler`. Swap to `beaconApi()` once
// `npm run codegen` runs against /openapi/v1.json.

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

interface GetSubscriptionsResult {
  entries: SubscriptionEntry[];
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
}

const SUBSCRIPTIONS_KEY = ['subscriptions'] as const;

export function useSubscriptionsQuery(search?: string) {
  return useQuery({
    queryKey: [...SUBSCRIPTIONS_KEY, search ?? null],
    queryFn: () => {
      const params = new URLSearchParams();
      if (search && search.trim()) {
        params.set('search', search.trim());
      }
      const qs = params.toString();
      return fetchJson<GetSubscriptionsResult>(
        `/beacon/api/subscriptions${qs ? `?${qs}` : ''}`,
      );
    },
  });
}

export function useCreateSubscription() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (values: CreateSubscriptionPayload) =>
      fetchJson<{ success: boolean; message: string | null }>(
        '/beacon/api/subscriptions',
        {
          method: 'POST',
          body: JSON.stringify(values),
        },
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY }),
  });
}

export function useDeleteSubscription() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY }),
  });
}

// ---------- Subscription detail ----------

/**
 * Wire-aligned with `Beacon.Core.Handlers.Subscriptions.SubscriptionDetail`.
 * Enum fields (`notificationTrigger`, `resultAttachmentType`, anomaly enums)
 * serialize as numeric ids — see `routes/recipients/queries.ts` for the
 * `NotificationTypeId` mapping that mirrors `Beacon.Core.Data.Enums`.
 */
export interface SubscriptionDetailRecipient {
  id: number;
  name: string;
  description: string | null;
  destination: string;
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

export interface SubscriptionDetail {
  id: number;
  queryId: number;
  queryName: string;
  status: string;
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
    queryFn: () =>
      fetchJson<GetSubscriptionDetailResult>(`/beacon/api/subscriptions/${id}`),
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

interface GetSubscriptionExecutionsResult {
  entries: SubscriptionExecutionEntry[];
  totalCount: number;
}

export function useSubscriptionExecutionsQuery(id: number | undefined, pageSize = 200) {
  return useQuery({
    queryKey: ['subscriptions', id, 'executions', pageSize] as const,
    queryFn: () =>
      fetchJson<GetSubscriptionExecutionsResult>(
        `/beacon/api/notifications?subscriptionId=${id}&pageSize=${pageSize}`,
      ),
    enabled: typeof id === 'number' && Number.isFinite(id),
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
    queryFn: () =>
      fetchJson<AnomalyChartResult>(
        `/beacon/api/subscriptions/${id}/anomaly-chart?days=${days}`,
      ),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

// ---------- Mutations ----------

export function useTestSubscription(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}/execute`, { method: 'POST' }),
    onSuccess: () => {
      if (typeof id === 'number') {
        qc.invalidateQueries({ queryKey: subscriptionDetailKey(id) });
        qc.invalidateQueries({ queryKey: ['subscriptions', id, 'executions'] });
        qc.invalidateQueries({ queryKey: ['subscriptions', id, 'anomaly-chart'] });
      }
      toast.success('Subscription executed');
    },
    onError: err => toast.error(describeError(err, 'Test run failed')),
  });
}

export function useArchiveSubscription(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      if (typeof id === 'number') {
        qc.invalidateQueries({ queryKey: subscriptionDetailKey(id) });
      }
      qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY });
      toast.success('Subscription archived');
    },
    onError: err => toast.error(describeError(err, 'Archive failed')),
  });
}

export function useAddSubscriptionRecipients(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (recipientIds: number[]) =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}/recipients`, {
        method: 'POST',
        body: JSON.stringify({ recipientIds }),
      }),
    onSuccess: () => {
      if (typeof id === 'number') {
        qc.invalidateQueries({ queryKey: subscriptionDetailKey(id) });
      }
      qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY });
      toast.success('Recipients added');
    },
    onError: err => toast.error(describeError(err, 'Add recipient failed')),
  });
}

export function useRemoveSubscriptionRecipient(id: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (recipientId: number) =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}/recipients/${recipientId}`, {
        method: 'DELETE',
      }),
    onSuccess: () => {
      if (typeof id === 'number') {
        qc.invalidateQueries({ queryKey: subscriptionDetailKey(id) });
      }
      qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY });
      toast.success('Recipient removed');
    },
    onError: err => toast.error(describeError(err, 'Remove recipient failed')),
  });
}

// ---------- Enum maps (mirror Beacon.Core.Data.Enums) ----------

export const NOTIFICATION_TRIGGER_LABEL: Record<number, { label: string; description: string }> = {
  1: { label: 'On change', description: 'When result count changes' },
  2: { label: 'Always', description: 'Every execution' },
  3: { label: 'On increase', description: 'When result count increases' },
};

export const NOTIFICATION_STATUS_LABEL: Record<number, string> = {
  1: 'Created',
  2: 'Sent',
  3: 'Silenced',
  4: 'No results',
  5: 'Timeout',
  6: 'Below threshold',
  7: 'Failed',
};

export const FILE_TYPE_LABEL: Record<number, string> = {
  1: 'CSV',
  2: 'XLSX',
};

export const ANOMALY_DETECTION_METHOD_LABEL: Record<number, string> = {
  1: 'Standard deviation',
  2: 'IQR',
  3: 'Percentage change',
};

export const ANOMALY_SENSITIVITY_LABEL: Record<number, string> = {
  1: 'Low',
  2: 'Medium',
  3: 'High',
};
