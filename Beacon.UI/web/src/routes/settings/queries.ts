import { useMutation, useQuery } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 4 — hand-typed wrappers; replace with `beaconApi()` after
// `npm run codegen`.

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
    queryFn: () => fetchJson<GetUserSettingsResult>('/beacon/api/user-settings'),
  });
}

export function useChangeOwnPassword() {
  return useMutation({
    mutationFn: (values: ChangeOwnPasswordPayload) =>
      fetchJson<void>('/beacon/api/user-settings/change-password', {
        method: 'POST',
        body: JSON.stringify(values),
      }),
  });
}
