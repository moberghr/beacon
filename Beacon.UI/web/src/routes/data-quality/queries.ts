import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { describeError, fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

export type ScoreTone = 'ok' | 'warn' | 'crit' | 'neutral';

export function scoreTone(score: number | null | undefined): ScoreTone {
  if (score === null || score === undefined) return 'neutral';
  if (score >= 90) return 'ok';
  if (score >= 70) return 'warn';
  return 'crit';
}

// Mirror Beacon.Core.Models.DataQuality.* and DataQualityEndpoints
export interface DataQualityScoreData {
  schemaName: string;
  tableName: string;
  score: number;
  trendDirection: number;
}

export interface DataQualityOverviewData {
  dataSourceId: number;
  dataSourceName: string;
  averageScore: number;
  totalTables: number;
  healthyTables: number;
  degradingTables: number;
  activeContracts: number;
  tableScores: DataQualityScoreData[];
}

export interface DataContractRuleData {
  id?: number;
  name: string;
  description?: string | null;
  ruleType: number;
  columnName?: string | null;
  configuration: string;
  severity: number;
  weight: number;
  isEnabled: boolean;
}

export interface DataContractRecipientData {
  id: number;
  name: string;
  destination?: string;
  notificationType?: number;
}

export interface DataContractData {
  id: number;
  dataSourceId: number;
  dataSourceName: string;
  schemaName: string;
  tableName: string;
  name: string;
  description?: string | null;
  cronExpression: string;
  isEnabled: boolean;
  ownerUserId?: string | null;
  alertOnFailure: boolean;
  failureThresholdScore: number;
  createdTime: string;
  latestScore: number | null;
  rules: DataContractRuleData[];
  recipients: DataContractRecipientData[];
}

export interface DataQualityRuleResultData {
  id: number;
  ruleName: string;
  passed: boolean;
  score: number;
  actualValue?: string | null;
  expectedValue?: string | null;
  message?: string | null;
  executionTimeMs: number;
}

export interface DataQualityEvaluationData {
  id: number;
  dataContractId: number;
  overallScore: number;
  passedRules: number;
  failedRules: number;
  totalRules: number;
  executionTimeMs: number;
  createdTime: string;
  ruleResults: DataQualityRuleResultData[];
}

export const DataContractRuleType = {
  Volume: 0,
  Freshness: 1,
  NullRate: 2,
  Uniqueness: 3,
  Referential: 4,
  Range: 5,
  Pattern: 6,
  CustomSql: 7,
} as const;

export const DataContractSeverity = {
  Low: 0,
  Medium: 1,
  High: 2,
  Critical: 3,
} as const;

export const DATA_QUALITY_OVERVIEW_KEY = ['data-quality', 'overview'] as const;
export const DATA_CONTRACTS_KEY = ['data-quality', 'contracts'] as const;
export const dataContractKey = (id: number) => ['data-quality', 'contract', id] as const;
export const evaluationHistoryKey = (id: number) => ['data-quality', 'contract', id, 'history'] as const;

export function useDataQualityOverview() {
  return useQuery({
    queryKey: DATA_QUALITY_OVERVIEW_KEY,
    queryFn: () => fetchJson<DataQualityOverviewData[]>('/beacon/api/data-quality/overview'),
  });
}

export function useDataContracts() {
  return useQuery({
    queryKey: DATA_CONTRACTS_KEY,
    queryFn: () => fetchJson<DataContractData[]>('/beacon/api/data-quality/contracts'),
  });
}

export function useDataContract(id: number | null) {
  return useQuery({
    queryKey: id === null ? ['data-quality', 'contract', 'null'] : dataContractKey(id),
    queryFn: () =>
      fetchJson<DataContractData>(`/beacon/api/data-quality/contracts/${id}`),
    enabled: id !== null,
  });
}

export function useEvaluationHistory(id: number | null) {
  return useQuery({
    queryKey: id === null ? ['data-quality', 'contract', 'null', 'history'] : evaluationHistoryKey(id),
    queryFn: () =>
      fetchJson<{ evaluations: DataQualityEvaluationData[] }>(
        `/beacon/api/data-quality/contracts/${id}/evaluations`,
      ),
    enabled: id !== null,
  });
}

export function useEvaluateContract(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, DataQualityEvaluationData>({
      qc,
      mutationFn: () =>
        fetchJson<DataQualityEvaluationData>(
          `/beacon/api/data-quality/contracts/${id}/evaluate`,
          { method: 'POST', body: '{}' },
        ),
      invalidate: [dataContractKey(id), evaluationHistoryKey(id), DATA_QUALITY_OVERVIEW_KEY],
      errorFallback: 'Evaluate contract failed',
    }),
  );
}

export function useDeleteContract() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`/beacon/api/data-quality/contracts/${id}`, { method: 'DELETE' }),
      invalidate: [DATA_CONTRACTS_KEY, DATA_QUALITY_OVERVIEW_KEY],
      errorFallback: 'Delete contract failed',
    }),
  );
}

export interface CreateContractPayload {
  dataSourceId: number;
  schemaName: string;
  tableName: string;
  name: string;
  description: string | null;
  cronExpression: string;
  isEnabled: boolean;
  ownerUserId: string | null;
  alertOnFailure: boolean;
  failureThresholdScore: number;
  rules: DataContractRuleData[];
  recipientIds: number[] | null;
}

export function useCreateContract() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateContractPayload, { dataContractId: number }>({
      qc,
      mutationFn: (payload) =>
        fetchJson<{ dataContractId: number }>('/beacon/api/data-quality/contracts', {
          method: 'POST',
          body: JSON.stringify(payload),
        }),
      invalidate: [DATA_CONTRACTS_KEY, DATA_QUALITY_OVERVIEW_KEY],
      errorFallback: 'Create contract failed',
    }),
  );
}

export function useUpdateContract(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<Omit<CreateContractPayload, 'dataSourceId'> & { dataSourceId: number }, void>({
      qc,
      mutationFn: (payload) =>
        fetchJson<void>(`/beacon/api/data-quality/contracts/${id}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        }),
      invalidate: [DATA_CONTRACTS_KEY, dataContractKey(id)],
      errorFallback: 'Update contract failed',
    }),
  );
}

export function describeContractError(err: unknown, fallback: string): string {
  return describeError(err, fallback);
}
