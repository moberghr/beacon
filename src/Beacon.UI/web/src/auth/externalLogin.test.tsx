import { screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { ApiError } from '@/lib/api';
import { AUTH_QUERY_KEY, createQueryClient } from '@/lib/queryClient';
import { renderWithProviders } from '@/test/render';
import LoginPage from '@/routes/auth/LoginPage';
import { mswServer } from '../../vitest.setup';
import { externalLoginHref, redirectToExternalLogin, type ExternalLogin } from './externalLogin';
import { RequireAuth } from './RequireAuth';
import type { CurrentUser } from './useAuth';

vi.mock('./externalLogin', async importOriginal => ({
  ...(await importOriginal<typeof import('./externalLogin')>()),
  redirectToExternalLogin: vi.fn(),
}));

const HOST_LOGIN: ExternalLogin = {
  loginUrl: '/Account/Login',
  returnUrlParameter: 'returnUrl',
};

function serveAnonymous(externalLogin: ExternalLogin | null) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: null,
        displayName: null,
        email: null,
        isAuthenticated: false,
        roles: [],
        realtimeEnabled: false,
        externalLogin,
      }),
    ),
    http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false })),
  );
}

function renderGuardedRoute() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/queries/5"
        element={
          <RequireAuth>
            <span>secret page</span>
          </RequireAuth>
        }
      />
      <Route path="/login" element={<LoginPage />} />
    </Routes>,
    { initialEntries: ['/queries/5'] },
  );
}

afterEach(() => {
  vi.mocked(redirectToExternalLogin).mockClear();
});

describe('externalLoginHref', () => {
  it('appends the return path under the configured parameter', () => {
    const href = externalLoginHref(HOST_LOGIN, '/beacon/queries/5?tab=runs');

    expect(href).toBe('/Account/Login?returnUrl=%2Fbeacon%2Fqueries%2F5%3Ftab%3Druns');
  });

  it('keeps query parameters the host login URL already has', () => {
    const href = externalLoginHref(
      { loginUrl: '/Account/Login?expired=true', returnUrlParameter: 'next' },
      '/beacon/home',
    );

    expect(href).toBe('/Account/Login?expired=true&next=%2Fbeacon%2Fhome');
  });
});

describe('external login mode', () => {
  it('sends a signed-out user to the host login instead of the Beacon login page', async () => {
    serveAnonymous(HOST_LOGIN);

    renderGuardedRoute();

    await waitFor(() => expect(redirectToExternalLogin).toHaveBeenCalledTimes(1));
    expect(vi.mocked(redirectToExternalLogin).mock.calls[0][0]).toEqual(HOST_LOGIN);
    expect(screen.queryByText('secret page')).toBeNull();
    expect(screen.queryByLabelText(/password/i)).toBeNull();
  });

  it('redirects from /login too, returning to the requested page under the router basename', async () => {
    serveAnonymous(HOST_LOGIN);

    renderWithProviders(<LoginPage />, {
      initialEntries: [{ pathname: '/login', state: { returnTo: '/queries/5' } } as never],
    });

    await waitFor(() => expect(redirectToExternalLogin).toHaveBeenCalledWith(HOST_LOGIN, '/queries/5'));
    expect(screen.queryByLabelText(/password/i)).toBeNull();
  });

  it('keeps the Beacon login page when the host does not own sign-in', async () => {
    serveAnonymous(null);

    renderGuardedRoute();

    expect(await screen.findByText(/sign in to your beacon workspace/i)).toBeInTheDocument();
    expect(redirectToExternalLogin).not.toHaveBeenCalled();
  });

  it('keeps the host login across a 401 so an expired session still goes to the host', () => {
    const client = createQueryClient();
    client.setQueryData<CurrentUser>(AUTH_QUERY_KEY, {
      userId: '42',
      displayName: 'Tester',
      email: null,
      isAuthenticated: true,
      roles: [],
      realtimeEnabled: true,
      externalLogin: HOST_LOGIN,
    });

    client.getQueryCache().config.onError?.(new ApiError(401, ''), undefined as never);

    const user = client.getQueryData<CurrentUser>(AUTH_QUERY_KEY);
    expect(user?.isAuthenticated).toBe(false);
    expect(user?.externalLogin).toEqual(HOST_LOGIN);
  });
});
