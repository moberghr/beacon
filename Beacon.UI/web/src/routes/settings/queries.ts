import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

export interface UserSettingsView {
  userName: string;
  email: string | null;
  displayName: string | null;
  isInternalUser: boolean;
  roles: string[];
}

interface GetUserSettingsResult {
  user: UserSettingsView;
}

export interface ChangeOwnPasswordPayload {
  currentPassword: string;
  newPassword: string;
}

const USER_SETTINGS_KEY = ['user-settings'] as const;

export function useUserSettingsQuery() {
  return useQuery({
    queryKey: USER_SETTINGS_KEY,
    queryFn: async () =>
      (await beaconApi().getUserSettings()) as unknown as GetUserSettingsResult,
  });
}

export function useChangeOwnPassword() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ChangeOwnPasswordPayload, void>({
      qc,
      mutationFn: (values) => beaconApi().changeOwnPassword(values),
      errorFallback: 'Change password failed',
    }),
  );
}
