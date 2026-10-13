import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';
import { ChangeApprovalKind } from '@/lib/enums';
import type { ApprovalStatus, MigrationMode, NotificationType } from '@/lib/enums';

// Local strict mirrors of the generated approval DTOs — dates are strings on
// the wire (see `unwrap` docs in @/lib/api).

export interface ApprovalRequestSummary {
  id: number;
  queryId: number;
  queryName: string;
  versionNumber: number;
  status: ApprovalStatus;
  requestedByUserName: string | null;
  createdTime: string;
  changeSummary: string | null;
}

export interface QueryStepParameterSnapshot {
  name: string;
  type: number;
  description: string | null;
  placeholder: string | null;
}

/** One step of a version, as the version recorded it. */
export interface QueryStepSnapshot {
  stepOrder: number;
  sqlValue: string;
  dataSourceId: number;
  dataSourceName: string;
  name: string | null;
  description: string | null;
  parameters: QueryStepParameterSnapshot[];
}

export interface QueryVersionDetail {
  id: number;
  versionNumber: number;
  label: string | null;
  status: number;
  name: string;
  description: string | null;
  finalQuery: string | null;
  createdTime: string;
  /** Author's display name or a neutral "User {id}" label; null when not recorded. */
  createdByUserName: string | null;
  changeSource: string | null;
  changeReason: string | null;
  steps?: QueryStepSnapshot[];
}

/** Wire-aligned with the backend `StepDiffType`. */
export const StepDiffType = {
  Unchanged: 0,
  Modified: 1,
  Added: 2,
  Removed: 3,
} as const;

export type StepDiffType = (typeof StepDiffType)[keyof typeof StepDiffType];

/** One position in the run: the active version's step there (A) and the proposal's (B). */
export interface StepDiff {
  stepOrder: number;
  diffType: StepDiffType;
  stepA: QueryStepSnapshot | null;
  stepB: QueryStepSnapshot | null;
}

export interface QueryVersionDiff {
  versionA: QueryVersionDetail | null;
  versionB: QueryVersionDetail | null;
  nameChanged: boolean;
  descriptionChanged: boolean;
  finalQueryChanged: boolean;
  stepDiffs?: StepDiff[];
}

export interface ApprovalRequestDetail {
  id: number;
  queryId: number;
  queryName: string;
  queryVersionId: number;
  status: ApprovalStatus;
  requestedByUserId: string | null;
  requestedByUserName: string | null;
  reviewedByUserName: string | null;
  reviewedAt: string | null;
  reviewComment: string | null;
  changeSummary: string | null;
  createdTime: string;
  proposedVersion: QueryVersionDetail;
  currentActiveVersion: QueryVersionDetail | null;
  autoDiff: QueryVersionDiff | null;
  /** The proposed content's hash: an approval sends it back, so it applies exactly what was shown. */
  contentHash?: string | null;
  /** The caller requested this change. */
  isOwnRequest?: boolean;
  /** Pending, and the caller may approve it (the server decides again on approval). */
  mayApprove?: boolean;
  /** Why the caller cannot approve this pending request; null when they may, or when it is not pending. */
  approveBlockedReason?: string | null;
  /** Pending, and the query's active version is no longer the one the change was made on. */
  isStale?: boolean;
}

const PENDING_KEY = ['approvals', 'pending'] as const;
const DETAIL_KEY = (id: number | undefined) => ['approvals', 'detail', id] as const;

/** Pending approval requests: server-paged, newest first. */
export function usePendingApprovalsList() {
  return usePagedList<ApprovalRequestSummary>({
    queryKey: PENDING_KEY,
    path: '/beacon/api/approvals/pending',
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

export function useApprovalDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: DETAIL_KEY(id),
    queryFn: async () =>
      unwrap<ApprovalRequestDetail>(await beaconApi().getApprovalDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

interface ApprovalActionArgs {
  id: number;
  comment: string | null;
}

interface ApproveArgs extends ApprovalActionArgs {
  /** The content hash of the detail the reviewer was shown: the approval applies exactly that content. */
  expectedContentHash: string | null;
}

export function useApproveQueryChange() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ApproveArgs, unknown>({
      qc,
      mutationFn: ({ id, comment, expectedContentHash }) =>
        beaconApi().approveQueryChange(id, { comment: comment ?? null, expectedContentHash }),
      // Approving moves the query's live steps to the proposal.
      invalidate: [['approvals'], ['query'], ['queries']],
      errorFallback: 'Approve query change failed',
    }),
  );
}

export function useRejectQueryChange() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ApprovalActionArgs, unknown>({
      qc,
      mutationFn: ({ id, comment }) =>
        beaconApi().rejectQueryChange(id, { comment: comment ?? null }),
      invalidate: [['approvals']],
      errorFallback: 'Reject query change failed',
    }),
  );
}

// --- changes of migration jobs, Custom SQL rules and recipients ---------------------------------------------------

/** A pending change of a migration job, a data contract's Custom SQL rules or a recipient, as listed. */
export interface ChangeApprovalSummary {
  id: number;
  kind: ChangeApprovalKind;
  resourceId: number;
  resourceName: string;
  status: ApprovalStatus;
  requestedByUserName: string | null;
  createdTime: string;
  changeSummary: string | null;
}

/** What a migration job does when it runs: the definition an approval covers. */
export interface MigrationJobDefinitionView {
  sourceDataSourceId: number;
  sourceDataSourceName: string | null;
  queryText: string;
  destinationDataSourceId: number;
  destinationDataSourceName: string | null;
  destinationTable: string;
  mode: MigrationMode;
  transformationScript: string | null;
}

export interface CustomSqlRuleView {
  name: string;
  sql: string;
  /** The rule waits for this change's approval (new, or its SQL or data source changed). */
  awaitsApproval: boolean;
}

export interface CustomSqlRulesView {
  dataSourceId: number;
  dataSourceName: string | null;
  rules: CustomSqlRuleView[];
}

/** A custom header's name and a short keyed fingerprint of its value (never the value). */
export interface RecipientHeaderView {
  name: string;
  fingerprint: string;
}

/**
 * Where a recipient delivers, as a reviewer is shown it: its type, what identifies its destination (host and path, Slack's
 * workspace and channel ids, a workflow path) with every credential masked, its header names with a fingerprint of each
 * value, and a fingerprint of the whole definition. Equal fingerprints mean equal values; they reveal nothing else.
 */
export interface RecipientDestinationView {
  notificationType: NotificationType;
  destination: string;
  headerNames: string[];
  headers?: RecipientHeaderView[];
  /** The fingerprint of the whole definition (type, destination and headers); null when it cannot be read. */
  fingerprint?: string | null;
  /** The stored destination or headers could not be decrypted, so nothing of them is shown. */
  unreadable?: boolean;
}

/**
 * The last decision on a governed resource's change, shown with the resource while it waits for approval and no request
 * of it is pending: a rejection (change it and save again; a migration job is deleted and created again) or a request a
 * newer save replaced, with the reviewer's comment.
 */
export interface ChangeApprovalOutcome {
  requestId: number;
  status: ApprovalStatus;
  comment: string | null;
  decidedAt: string | null;
}

export interface ChangeApprovalDetail {
  id: number;
  kind: ChangeApprovalKind;
  resourceId: number;
  resourceName: string;
  /** The resource still exists: a deleted one's change can be rejected, not approved. */
  resourceExists: boolean;
  status: ApprovalStatus;
  requestedByUserName: string | null;
  reviewedByUserName: string | null;
  reviewedAt: string | null;
  reviewComment: string | null;
  changeSummary: string | null;
  createdTime: string;
  previousMigrationJob: MigrationJobDefinitionView | null;
  proposedMigrationJob: MigrationJobDefinitionView | null;
  previousCustomSqlRules: CustomSqlRulesView | null;
  proposedCustomSqlRules: CustomSqlRulesView | null;
  previousRecipientDestination: RecipientDestinationView | null;
  proposedRecipientDestination: RecipientDestinationView | null;
  /** The hash of what this detail shows as proposed: an approval sends it back, so it applies exactly what was shown. */
  payloadHash: string;
  isOwnRequest: boolean;
  /** Pending, and the caller may approve it (the server decides again on approval). */
  mayApprove: boolean;
  /** Why the caller cannot approve this pending request; null when they may, or when it is not pending. */
  approveBlockedReason: string | null;
  /** Pending, and the resource no longer holds the definition it proposes (or no longer exists). */
  isStale: boolean;
  /**
   * A recipient change whose before and after look the same (type, destination as shown, header names) while the
   * fingerprints differ: the change lies in a part the mask hides, such as a token or a header value.
   */
  changeHiddenByMask?: boolean;
  /** Approving requires a comment: what changed cannot be seen, so the reviewer records how they checked it. */
  commentRequiredToApprove?: boolean;
}

/** What each kind of change is called. */
export const CHANGE_KIND_LABEL: Record<ChangeApprovalKind, string> = {
  [ChangeApprovalKind.MigrationJob]: 'Migration job',
  [ChangeApprovalKind.CustomSqlRules]: 'Custom SQL rules',
  [ChangeApprovalKind.RecipientDestination]: 'Recipient',
};

const CHANGES_KEY = ['approvals', 'changes'] as const;
const CHANGE_DETAIL_KEY = (id: number | undefined) => ['approvals', 'changes', 'detail', id] as const;

/** Pending changes of one kind: server-paged, newest first. */
export function useChangeApprovalsList(kind: ChangeApprovalKind) {
  return usePagedList<ChangeApprovalSummary>({
    queryKey: [...CHANGES_KEY, kind],
    path: '/beacon/api/approvals/changes',
    fixedParams: { kind },
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

export function useChangeApprovalDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: CHANGE_DETAIL_KEY(id),
    queryFn: async () =>
      unwrap<ChangeApprovalDetail>(await beaconApi().getChangeApprovalDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

interface ChangeApproveArgs extends ApprovalActionArgs {
  /** The payload hash of the detail the reviewer was shown: the approval applies exactly that. */
  expectedPayloadHash: string | null;
}

// A decision changes what the governed pages show (awaiting badges, the run and delivery state).
const GOVERNED_KEYS = [['approvals'], ['migration-jobs'], ['recipients'], ['data-quality'], ['subscriptions']];

export function useApproveChange() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ChangeApproveArgs, unknown>({
      qc,
      mutationFn: ({ id, comment, expectedPayloadHash }) =>
        beaconApi().approveChange(id, { comment: comment ?? null, expectedPayloadHash }),
      invalidate: GOVERNED_KEYS,
      errorFallback: 'Approve change failed',
    }),
  );
}

export function useRejectChange() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ApprovalActionArgs, unknown>({
      qc,
      mutationFn: ({ id, comment }) => beaconApi().rejectChange(id, { comment: comment ?? null }),
      invalidate: GOVERNED_KEYS,
      errorFallback: 'Reject change failed',
    }),
  );
}
