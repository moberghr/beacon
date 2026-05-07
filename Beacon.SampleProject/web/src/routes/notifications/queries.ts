import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export function useNotificationsQuery() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => beaconApi().getNotifications(0, 100, undefined, undefined),
  });
}
