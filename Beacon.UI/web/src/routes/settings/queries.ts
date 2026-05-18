import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

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
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<ChangeOwnPasswordPayload, void>({
      qc,
      mutationFn: (values) =>
        fetchJson<void>('/beacon/api/user-settings/change-password', {
          method: 'POST',
          body: JSON.stringify(values),
        }),
      errorFallback: 'Change password failed',
    }),
  );
}
