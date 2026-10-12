import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { httpErrorInfo, unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';
import { AUTH_QUERY_KEY, UNAUTHENTICATED_USER } from '@/lib/queryClient';
import type { CurrentUser } from '@/auth/useAuth';

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
      unwrap<GetUserSettingsResult>(await beaconApi().getUserSettings()),
  });
}

/**
 * A 401 means the password was changed but this session was signed out at the same time (another change of the
 * user's sessions came in between): the cached session is dropped, so the user is sent to sign in again.
 */
export function useChangeOwnPassword() {
  const qc = useQueryClient();
  const options = createSimpleMutation<ChangeOwnPasswordPayload, void>({
    qc,
    mutationFn: (values) => beaconApi().changeOwnPassword(values),
    errorFallback: 'Change password failed',
  });
  return useMutation({
    ...options,
    onError: (...args) => {
      options.onError?.(...args);
      if (httpErrorInfo(args[0])?.status === 401) {
        qc.setQueryData<CurrentUser>(AUTH_QUERY_KEY, (previous) => ({
          ...UNAUTHENTICATED_USER,
          externalLogin: previous?.externalLogin ?? null,
        }));
      }
    },
  });
}
