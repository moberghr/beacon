import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useQueryVersionsQuery(queryId: number | undefined) {
  return useQuery({
    queryKey: ['queries', queryId, 'versions'],
    queryFn: () => beaconApi().getQueryVersions(queryId as number),
    enabled: typeof queryId === 'number' && Number.isFinite(queryId),
  });
}
