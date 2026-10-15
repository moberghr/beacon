import { type ReactNode } from 'react';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { AUTH_QUERY_KEY } from '@/lib/queryClient';
import type { CurrentUser } from '@/auth/useAuth';
import { useChangeOwnPassword } from './queries';

const SIGNED_IN: CurrentUser = {
  userId: '1',
  displayName: 'Ana',
  email: null,
  isAuthenticated: true,
  roles: ['Viewer'],
  realtimeEnabled: true,
  externalLogin: { loginUrl: 'https://host.example/login', returnUrlParameter: 'returnUrl' },
};

function setup(status: number) {
  mswServer.use(
    http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
    http.post('*/beacon/api/user-settings/change-password', () =>
      status === 204
        ? new HttpResponse(null, { status: 204 })
        : HttpResponse.json({ title: 'Unauthorized', detail: 'Sign in again with your new password.' }, { status })),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  client.setQueryData(AUTH_QUERY_KEY, SIGNED_IN);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useChangeOwnPassword(), { wrapper });

  return { client, result };
}

describe('useChangeOwnPassword', () => {
  it('drops the cached session when the change answers 401, so the user signs in again', async () => {
    const { client, result } = setup(401);

    await expect(result.current.mutateAsync({ currentPassword: 'old', newPassword: 'New-password-1!' })).rejects.toBeDefined();

    expect(client.getQueryData(AUTH_QUERY_KEY)).toMatchObject({
      isAuthenticated: false,
      externalLogin: SIGNED_IN.externalLogin,
    });
  });

  it.each([204, 400])('keeps the session when the change answers %i', async (status) => {
    const { client, result } = setup(status);

    await result.current.mutateAsync({ currentPassword: 'old', newPassword: 'New-password-1!' }).catch(() => undefined);

    expect(client.getQueryData(AUTH_QUERY_KEY)).toMatchObject({ isAuthenticated: true });
  });
});
