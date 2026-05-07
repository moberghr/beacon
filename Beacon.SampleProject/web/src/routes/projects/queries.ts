import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useProjectsQuery() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => beaconApi().getProjects(),
  });
}
