import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';
import type { ChangeApprovalOutcome } from '@/routes/approvals/queries';

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
  /** Enabled contracts on the source whose scheduled evaluations are skipped: their owner may no longer run them. */
  ownerNotAuthorizedContracts: number;
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
  /** Read only: with the approval workflow on, this Custom SQL rule's SQL waits for approval, so evaluations leave it out. */
  awaitingApproval?: boolean;
}

export interface DataContractRecipientData {
  id: number;
  name: string;
  /** Always null: destinations are secrets, shown masked to admins on the recipients page only. */
  destination?: string | null;
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
  /** An owner is recorded: scheduled evaluations run as the owner, checked at every run. */
  hasOwner: boolean;
  /** The owner's display name or a neutral "User {id}" label; null when no owner or no matching user. Never an id. */
  ownerDisplayName: string | null;
  /**
   * A scheduled evaluation was skipped because the owner may no longer run the contract's rules, and none has run since:
   * its scheduled checks are not happening.
   */
  ownerNotAuthorized: boolean;
  alertOnFailure: boolean;
  failureThresholdScore: number;
  createdTime: string;
  latestScore: number | null;
  rules: DataContractRuleData[];
  recipients: DataContractRecipientData[];
  /** The pending change request of the contract's Custom SQL rules, if one waits (an Approver opens it). */
  pendingApprovalRequestId?: number | null;
  /** While Custom SQL rules wait and no request of them is pending: the last decision (a rejection: change and save again). */
  lastApprovalDecision?: ChangeApprovalOutcome | null;
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
  /** The enabled Custom SQL rules left out because their SQL waits for approval; more than none makes the score partial. */
  awaitingApprovalRules?: number;
  /** The score leaves out rules that wait for approval: it does not stand for the whole contract. */
  isPartial?: boolean;
  /** The alert recipients this evaluation's alert skipped because where they deliver waits for approval. */
  heldAlertRecipients?: number;
  executionTimeMs: number;
  createdTime: string;
  ruleResults: DataQualityRuleResultData[];
}

/** What an evaluation left out or held back, in a sentence; null when it left out nothing and held no alert. */
export function evaluationNote(evaluation: Pick<DataQualityEvaluationData, 'isPartial' | 'awaitingApprovalRules' | 'heldAlertRecipients'>): string | null {
  const parts: string[] = [];
  const awaiting = evaluation.awaitingApprovalRules ?? 0;
  if (evaluation.isPartial || awaiting > 0) {
    parts.push(
      `Partial: ${awaiting} Custom SQL rule${awaiting === 1 ? '' : 's'} awaiting approval ${awaiting === 1 ? 'was' : 'were'} not evaluated`,
    );
  }

  const held = evaluation.heldAlertRecipients ?? 0;
  if (held > 0) {
    parts.push(`the alert skipped ${held} recipient${held === 1 ? '' : 's'} whose destination awaits approval`);
  }

  if (parts.length === 0) {
    return null;
  }

  const sentence = parts.join('; ');

  return `${sentence.charAt(0).toUpperCase()}${sentence.slice(1)}.`;
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

/** The data contracts grid: server-paged, newest first. */
export function useDataContractsList() {
  return usePagedList<DataContractData>({
    queryKey: DATA_CONTRACTS_KEY,
    path: '/beacon/api/data-quality/contracts',
    defaultSort: { column: 'createdTime', direction: 'desc' },
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

/** Admin only: makes a Beacon user the contract's owner. */
export function useSetContractOwner(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (userId) => beaconApi().setDataContractOwner(id, { userId }),
      invalidate: [DATA_CONTRACTS_KEY, dataContractKey(id)],
      successMsg: 'Owner updated',
      errorFallback: 'Changing the owner failed',
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
  alertOnFailure: boolean;
  failureThresholdScore: number;
  rules: DataContractRuleData[];
  recipientIds: number[] | null;
}

export function useCreateContract() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateContractPayload, { dataContractId: number; awaitingApproval: boolean; message: string | null }>({
      qc,
      mutationFn: async (payload) => {
        const result = await beaconApi().createDataContract(payload as never);
        return {
          dataContractId: result.dataContractId ?? 0,
          awaitingApproval: result.awaitingApproval ?? false,
          message: result.message ?? null,
        };
      },
      invalidate: [DATA_CONTRACTS_KEY, DATA_QUALITY_OVERVIEW_KEY],
      errorFallback: 'Create contract failed',
      // The contract dialog shows the server's reason in a banner.
      errorToast: false,
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
      // The contract dialog shows the server's reason in a banner.
      errorToast: false,
    }),
  );
}
