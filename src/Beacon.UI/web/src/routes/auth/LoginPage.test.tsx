import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../../vitest.setup';
import LoginPage from './LoginPage';

const anonymousMe = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({
    userId: null,
    displayName: null,
    email: null,
    isAuthenticated: false,
    roles: [],
  }),
);

const SSO_LABEL = /continue with single sign-on/i;

describe('LoginPage SSO button', () => {
  it('hides the SSO button when SSO is not configured', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false })),
    );

    renderWithProviders(<LoginPage />);

    // The username field is always present — once it renders, the SSO query has settled.
    await waitFor(() => expect(screen.getByPlaceholderText('you@moberg.hr')).toBeInTheDocument());
    expect(screen.queryByText(SSO_LABEL)).not.toBeInTheDocument();
  });

  it('shows the SSO button when SSO is configured', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true })),
    );

    renderWithProviders(<LoginPage />);

    const ssoLink = await screen.findByText(SSO_LABEL);
    expect(ssoLink.closest('a')).toHaveAttribute('href', '/beacon/api/auth/sso/challenge');
  });
});

describe('LoginPage SSO errors', () => {
  it('tells a refused SSO account that it is not permitted', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true })),
    );

    renderWithProviders(<LoginPage />, { initialEntries: ['/login?ssoError=not_admitted'] });

    expect(await screen.findByText(/not permitted to use beacon/i)).toBeInTheDocument();
  });

  it('keeps the generic message for other SSO failures', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true })),
    );

    renderWithProviders(<LoginPage />, { initialEntries: ['/login?ssoError=1'] });

    expect(await screen.findByText(/single sign-on failed/i)).toBeInTheDocument();
  });
});

// A local sign-in ends with a full page load, which the router never sees, so the target must
// carry the /beacon basename itself — otherwise the browser lands on the host's own /home.
describe('LoginPage local sign-in redirect', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function signInUnderBasename(redirectUrl: string | null, returnTo?: string) {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false })),
      http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('*/beacon/api/auth/login', () => HttpResponse.json({ success: true, redirectUrl })),
    );
    // MSW resolves relative fetch URLs against location.href, so reads stay absolute; writes are the navigation.
    const navigation = { to: null as string | null };
    vi.stubGlobal('location', {
      origin: 'http://localhost:3000',
      get href() {
        return 'http://localhost:3000/beacon/login';
      },
      set href(value: string) {
        navigation.to = value;
      },
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 0, gcTime: 0 } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter basename="/beacon" initialEntries={[{ pathname: '/beacon/login', state: returnTo ? { returnTo } : null }]}>
          <LoginPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    return navigation;
  }

  async function submitCredentials() {
    fireEvent.change(await screen.findByPlaceholderText('you@moberg.hr'), { target: { value: 'local.user' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));
  }

  it('returns to the requested page under the router basename', async () => {
    const navigation = signInUnderBasename('/beacon/home', '/queries/5?tab=runs');

    await submitCredentials();

    await waitFor(() => expect(navigation.to).toBe('/beacon/queries/5?tab=runs'));
  });

  it('falls back to home under the router basename when the server sends no redirect', async () => {
    const navigation = signInUnderBasename(null);

    await submitCredentials();

    await waitFor(() => expect(navigation.to).toBe('/beacon/home'));
  });
});
