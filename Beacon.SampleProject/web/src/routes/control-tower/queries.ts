import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useControlTowerQuery() {
  return useQuery({
    queryKey: ['control-tower'],
    queryFn: async () => {
      const [stats, health] = await Promise.all([
        beaconApi().getControlTowerStatistics(),
        beaconApi().getControlTowerHealth(0, 200, undefined, undefined, undefined, undefined, undefined),
      ]);
      return { stats: stats.statistics, entries: health.entries };
    },
  });
}
