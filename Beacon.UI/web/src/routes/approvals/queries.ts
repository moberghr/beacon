import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
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

export interface QueryVersionDetail {
  id: number;
  versionNumber: number;
  label: string | null;
  status: number;
  name: string;
  description: string | null;
  finalQuery: string | null;
  createdTime: string;
  createdByUserId: string | null;
  changeSource: string | null;
  changeReason: string | null;
}

export interface QueryVersionDiff {
  versionA: QueryVersionDetail | null;
  versionB: QueryVersionDetail | null;
  nameChanged: boolean;
  descriptionChanged: boolean;
  finalQueryChanged: boolean;
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
}

const PENDING_KEY = ['approvals', 'pending'] as const;
const DETAIL_KEY = (id: number | undefined) => ['approvals', 'detail', id] as const;

export function usePendingApprovalsQuery() {
  return useQuery({
    queryKey: PENDING_KEY,
    queryFn: async () =>
      unwrap<ApprovalRequestSummary[]>(await beaconApi().getPendingApprovals(undefined)),
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

export function useApproveQueryChange() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ApprovalActionArgs, unknown>({
      qc,
      mutationFn: ({ id, comment }) =>
        beaconApi().approveQueryChange(id, { comment: comment ?? null }),
      invalidate: [['approvals']],
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
