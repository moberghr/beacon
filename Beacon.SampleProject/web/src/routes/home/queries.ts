import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useHomeStatsQuery() {
  return useQuery({
    queryKey: ['home', 'stats'],
    queryFn: async () => {
      const [projects, ct, notifications] = await Promise.all([
        beaconApi().getProjects(),
        beaconApi().getControlTowerStatistics(),
        beaconApi().getNotifications(0, 1, undefined, undefined),
      ]);
      return { projects, controlTower: ct.statistics, notifications };
    },
  });
}
