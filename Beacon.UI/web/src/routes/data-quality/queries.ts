import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
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

export const DATA_QUALITY_OVERVIEW_KEY = ['data-quality', 'overview'] as const;
export const DATA_CONTRACTS_KEY = ['data-quality', 'contracts'] as const;
export const dataContractKey = (id: number) => ['data-quality', 'contract', id] as const;
export const evaluationHistoryKey = (id: number) => ['data-quality', 'contract', id, 'history'] as const;

// Generated DTOs use `?:` everywhere; the hand-typed interfaces in this
// file are stricter (most fields required). We cast at the boundary —
// runtime shape is identical, only the TS types differ.

export function useDataQualityOverview() {
  return useQuery({
    queryKey: DATA_QUALITY_OVERVIEW_KEY,
    queryFn: async () =>
      unwrap<DataQualityOverviewData[]>(await beaconApi().getDataQualityOverview(undefined)),
  });
}

export function useDataContracts() {
  return useQuery({
    queryKey: DATA_CONTRACTS_KEY,
    queryFn: async () =>
      unwrap<DataContractData[]>(await beaconApi().getDataContracts(undefined)),
  });
}

export function useDataContract(id: number | null) {
  return useQuery({
    queryKey: id === null ? ['data-quality', 'contract', 'null'] : dataContractKey(id),
    queryFn: async () =>
      unwrap<DataContractData>(await beaconApi().getDataContractDetail(id as number)),
    enabled: id !== null,
  });
}

export function useEvaluationHistory(id: number | null) {
  return useQuery({
    queryKey: id === null ? ['data-quality', 'contract', 'null', 'history'] : evaluationHistoryKey(id),
    queryFn: async () =>
      unwrap<{ evaluations: DataQualityEvaluationData[] }>(
        await beaconApi().getEvaluationHistory(id as number, undefined),
      ),
    enabled: id !== null,
  });
}

export function useEvaluateContract(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, DataQualityEvaluationData>({
      qc,
      mutationFn: async () =>
        unwrap<DataQualityEvaluationData>(await beaconApi().evaluateDataContract(id)),
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
      mutationFn: (id) => beaconApi().deleteDataContract(id),
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
      mutationFn: async (payload) => {
        const result = await beaconApi().createDataContract(payload as never);
        return { dataContractId: result.dataContractId ?? 0 };
      },
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
      mutationFn: (payload) => beaconApi().updateDataContract(id, payload as never),
      invalidate: [DATA_CONTRACTS_KEY, dataContractKey(id), DATA_QUALITY_OVERVIEW_KEY],
      errorFallback: 'Update contract failed',
    }),
  );
}
