import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import type { ApprovalRequestSummary, ApprovalRequestDetail } from '@/api/generated/beacon-api';

export type { ApprovalRequestSummary, ApprovalRequestDetail };

const PENDING_KEY = ['approvals', 'pending'] as const;
const DETAIL_KEY = (id: number | undefined) => ['approvals', 'detail', id] as const;

export function usePendingApprovalsQuery() {
  return useQuery({
    queryKey: PENDING_KEY,
    queryFn: () => beaconApi().getPendingApprovals(undefined),
  });
}

export function useApprovalDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: DETAIL_KEY(id),
    queryFn: () => beaconApi().getApprovalDetail(id as number),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

interface ApprovalActionArgs {
  id: number;
  comment: string | null;
}

export function useApproveQueryChange() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, comment }: ApprovalActionArgs) =>
      beaconApi().approveQueryChange(id, {
        reviewerUserId: null,
        reviewerName: null,
        comment: comment ?? null,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['approvals'] }),
  });
}

export function useRejectQueryChange() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, comment }: ApprovalActionArgs) =>
      beaconApi().rejectQueryChange(id, {
        reviewerUserId: null,
        reviewerName: null,
        comment: comment ?? null,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['approvals'] }),
  });
}
