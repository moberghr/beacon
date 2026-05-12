import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { beaconApi } from '@/api/client';
import type { CreateAiActorCommand } from '@/api/generated/beacon-api';

export const ACTOR_STATUS_LABEL: Record<number, string> = {
  0: 'Draft',
  1: 'Active',
  2: 'Paused',
  3: 'Disabled',
  4: 'Archived',
};

export function useAiActorsQuery(dataSourceId: number | undefined, includeArchived: boolean) {
  return useQuery({
    queryKey: ['ai-actors', dataSourceId ?? null, includeArchived],
    queryFn: () => beaconApi().getAiActorList(dataSourceId!, includeArchived),
    enabled: dataSourceId !== undefined && dataSourceId > 0,
  });
}

export function useAiActorDetailsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['ai-actor', id ?? 0],
    queryFn: () => beaconApi().getAiActorDetails(id!, 10),
    enabled: id !== undefined && id > 0,
  });
}

export function useCreateAiActor(dataSourceId: number | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (cmd: CreateAiActorCommand) => beaconApi().createAiActor(cmd),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ai-actors', dataSourceId ?? null] });
      toast.success('AI actor created');
    },
    onError: () => {
      toast.error('Failed to create AI actor');
    },
  });
}
