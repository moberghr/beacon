import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';
import type { ApprovalStatus } from '@/lib/enums';

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
