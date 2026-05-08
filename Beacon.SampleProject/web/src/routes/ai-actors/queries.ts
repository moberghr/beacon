import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

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
