import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useMigrationExecutionsQuery() {
  return useQuery({
    queryKey: ['migration-executions'],
    queryFn: () =>
      beaconApi().getMigrationExecutions(undefined, undefined, undefined, undefined, 0, 100),
  });
}
