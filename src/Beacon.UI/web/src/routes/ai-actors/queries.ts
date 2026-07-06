import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
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

export interface GetAiActorListResult {
  actors: AiActorListItem[];
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
  createdByUserId: string | null;
  defaultRecipientIds: number[] | null;
  activateImmediately: boolean;
}

export function useAiActorsQuery(dataSourceId: number | undefined, includeArchived: boolean) {
  return useQuery({
    queryKey: ['ai-actors', dataSourceId ?? null, includeArchived],
    queryFn: async () =>
      unwrap<GetAiActorListResult>(await beaconApi().getAiActorList(dataSourceId!, includeArchived)),
    enabled: dataSourceId !== undefined && dataSourceId > 0,
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
