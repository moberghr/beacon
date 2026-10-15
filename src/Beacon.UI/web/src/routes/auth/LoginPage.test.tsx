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

  it('tells the user when the sign-in did not use multi-factor authentication', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true })),
    );

    renderWithProviders(<LoginPage />, { initialEntries: ['/login?ssoError=mfa_required'] });

    expect(await screen.findByText(/did not use multi-factor authentication/i)).toBeInTheDocument();
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

describe('LoginPage with local login off', () => {
  it('offers only single sign-on, without the password form', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true, localLoginEnabled: false })),
    );

    renderWithProviders(<LoginPage />);

    expect(await screen.findByText(SSO_LABEL)).toBeInTheDocument();
    expect(screen.queryByPlaceholderText('you@moberg.hr')).not.toBeInTheDocument();
    expect(screen.queryByText(/or sign in with username/i)).not.toBeInTheDocument();
  });

  it('says that password sign-in is off when there is no single sign-on either', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false, localLoginEnabled: false })),
    );

    renderWithProviders(<LoginPage />);

    expect(await screen.findByText(/password sign-in is turned off/i)).toBeInTheDocument();
    expect(screen.queryByPlaceholderText('you@moberg.hr')).not.toBeInTheDocument();
  });

  it('shows the password form to a break-glass account through ?local=1', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true, localLoginEnabled: false })),
    );

    renderWithProviders(<LoginPage />, { initialEntries: ['/login?local=1'] });

    expect(await screen.findByPlaceholderText('you@moberg.hr')).toBeInTheDocument();
  });
});

describe('LoginPage when an administrator set the password', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('asks for a new password, then signs in with it', async () => {
    const bodies: Record<string, unknown>[] = [];
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false, localLoginEnabled: true })),
      http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('*/beacon/api/auth/login', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        bodies.push(body);
        return body.newPassword
          ? HttpResponse.json({ success: true, redirectUrl: '/home' })
          : HttpResponse.json(
              { success: false, passwordChangeRequired: true, error: 'Choose a new password.' },
              { status: 401 },
            );
      }),
    );
    const navigation = { to: null as string | null };
    vi.stubGlobal('location', {
      origin: 'http://localhost:3000',
      get href() {
        return 'http://localhost:3000/login';
      },
      set href(value: string) {
        navigation.to = value;
      },
    });

    renderWithProviders(<LoginPage />);
    fireEvent.change(await screen.findByPlaceholderText('you@moberg.hr'), { target: { value: 'newbie' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'admin-set' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText(/choose your/i)).toBeInTheDocument();
    const fields = screen.getAllByLabelText(/new password/i);
    fireEvent.change(fields[0], { target: { value: 'Mine-1!xyz' } });
    fireEvent.change(fields[1], { target: { value: 'Mine-1!xyw' } });
    fireEvent.click(screen.getByRole('button', { name: /set password and sign in/i }));
    expect(await screen.findByText(/do not match/i)).toBeInTheDocument();

    fireEvent.change(fields[1], { target: { value: 'Mine-1!xyz' } });
    fireEvent.click(screen.getByRole('button', { name: /set password and sign in/i }));

    await waitFor(() => expect(navigation.to).not.toBeNull());
    expect(bodies).toHaveLength(2);
    expect(bodies[1]).toMatchObject({ username: 'newbie', password: 'admin-set', newPassword: 'Mine-1!xyz' });
  });

  it('keeps asking, with the reason, when the new password is refused', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false, localLoginEnabled: true })),
      http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('*/beacon/api/auth/login', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(
          {
            success: false,
            passwordChangeRequired: true,
            error: body.newPassword ? 'Password must be at least 12 characters long.' : 'Choose a new password.',
          },
          { status: body.newPassword ? 400 : 401 },
        );
      }),
    );

    renderWithProviders(<LoginPage />);
    fireEvent.change(await screen.findByPlaceholderText('you@moberg.hr'), { target: { value: 'newbie' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'admin-set' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));
    const fields = await screen.findAllByLabelText(/new password/i);
    fireEvent.change(fields[0], { target: { value: 'short' } });
    fireEvent.change(fields[1], { target: { value: 'short' } });
    fireEvent.click(screen.getByRole('button', { name: /set password and sign in/i }));

    expect(await screen.findByText(/at least 12 characters/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /set password and sign in/i })).toBeInTheDocument();
  });
  it('returns to the sign-in form, with the error, when the change is refused for another reason', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false, localLoginEnabled: true })),
      http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('*/beacon/api/auth/login', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        // The account was disabled between the two requests: the generic answer, no change asked.
        return body.newPassword
          ? HttpResponse.json({ success: false, error: 'Invalid username or password.' }, { status: 401 })
          : HttpResponse.json(
              { success: false, passwordChangeRequired: true, error: 'Choose a new password.' },
              { status: 401 },
            );
      }),
    );

    renderWithProviders(<LoginPage />);
    fireEvent.change(await screen.findByPlaceholderText('you@moberg.hr'), { target: { value: 'newbie' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'admin-set' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));
    const fields = await screen.findAllByLabelText(/new password/i);
    fireEvent.change(fields[0], { target: { value: 'Mine-1!xyz' } });
    fireEvent.change(fields[1], { target: { value: 'Mine-1!xyz' } });
    fireEvent.click(screen.getByRole('button', { name: /set password and sign in/i }));

    expect(await screen.findByText('Invalid username or password.')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('you@moberg.hr')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /set password and sign in/i })).not.toBeInTheDocument();
  });
});
