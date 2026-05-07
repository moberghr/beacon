import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useProjectsQuery() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => beaconApi().getProjects(),
  });
}

export function useProjectDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id],
    queryFn: () => beaconApi().getProjectDetail(id as number),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}
