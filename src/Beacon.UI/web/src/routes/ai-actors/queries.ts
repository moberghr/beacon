import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { AiActorStatus } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const ACTOR_STATUS_LABEL: Record<number, string> = {
  [AiActorStatus.Draft]: 'Draft',
  [AiActorStatus.Active]: 'Active',
  [AiActorStatus.Paused]: 'Paused',
  [AiActorStatus.Failed]: 'Failed',
  [AiActorStatus.Archived]: 'Archived',
};

// Local strict interfaces bridged via unwrap<T>() — the generated DTOs are
// intentionally loose (optional everywhere, Date-typed fields that are
// strings on the wire). See src/lib/api.ts.
export interface AiActorListItem {
  actorId: number;
  name: string;
  instructions: string;
  dataSourceId: number;
  dataSourceName: string;
  status: AiActorStatus;
  thinkCount: number;
  lastThinkTime: string | null;
  totalCost: number;
  createdTime: string;
}

export interface AiActorDetails {
  actorId: number;
  name: string;
  instructions: string;
  additionalContext: string | null;
  dataSourceId: number;
  dataSourceName: string;
  status: AiActorStatus;
  maxQueries: number;
  maxSubscriptionsPerQuery: number;
  requiresApproval: boolean;
  totalTokensUsed: number;
  totalCost: number;
  lastThinkTime: string | null;
  thinkCount: number;
  lastError: string | null;
  createdTime: string;
  pendingPlanCount: number;
}

export interface CreateAiActorPayload {
  name: string;
  instructions: string;
  dataSourceId: number;
  additionalContext: string | null;
  maxQueries: number | null;
  maxSubscriptionsPerQuery: number | null;
  defaultRecipientIds: number[] | null;
  activateImmediately: boolean;
}

/** The AI actors grid: server-paged, newest first; data source and archived filters in the URL. */
export function useAiActorsList() {
  return usePagedList<AiActorListItem, { dataSourceId: string; includeArchived: string }>({
    queryKey: ['ai-actors'],
    path: '/beacon/api/ai-actors',
    filters: { dataSourceId: '', includeArchived: '' },
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

export function useAiActorDetailsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['ai-actor', id ?? 0],
    queryFn: async () =>
      unwrap<AiActorDetails>(await beaconApi().getAiActorDetails(id!, 10)),
    enabled: id !== undefined && id > 0,
  });
}

/** Admin only: makes a Beacon user the actor's creator, who may then change and run it. */
export function useSetAiActorOwner(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (userId) => beaconApi().setAiActorOwner(id, { userId }),
      invalidate: [['ai-actor', id], ['ai-actors']],
      successMsg: 'Owner updated',
      errorFallback: 'Changing the owner failed',
    }),
  );
}

export function useCreateAiActor() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateAiActorPayload, unknown>({
      qc,
      mutationFn: (cmd) => beaconApi().createAiActor(cmd),
      // The dialog lets the user pick any data source — invalidate the whole
      // ['ai-actors'] prefix, not just one data source's list.
      invalidate: [['ai-actors']],
      successMsg: 'AI actor created',
      errorFallback: 'Failed to create AI actor',
    }),
  );
}
