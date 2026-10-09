import { fireEvent, screen, waitFor } from '@testing-library/react';
import { Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../vitest.setup';
import { RequireAuth } from './RequireAuth';

function serveUser(isAuthenticated: boolean) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: isAuthenticated ? 'sso-user' : null,
        displayName: isAuthenticated ? 'New Person' : null,
        email: null,
        isAuthenticated,
        roles: [],
        realtimeEnabled: false,
        externalLogin: null,
      }),
    ),
  );
}

function serveSignedIn(canRead: boolean) {
  serveUser(true);
  mswServer.use(http.get('*/beacon/api/auth/permissions', () => HttpResponse.json({ canRead, canWrite: false })));
}

function LogoutProbe() {
  const location = useLocation();
  return <span>logout page {JSON.stringify(location.state)}</span>;
}

function renderGuardedPage() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/home"
        element={
          <RequireAuth>
            <span>workspace</span>
          </RequireAuth>
        }
      />
      <Route path="/login" element={<span>login page</span>} />
      <Route path="/logout" element={<LogoutProbe />} />
    </Routes>,
    { initialEntries: ['/home'] },
  );
}

describe('RequireAuth for a signed-in user', () => {
  it('explains that a user without a role has no access yet', async () => {
    serveSignedIn(false);

    renderGuardedPage();

    expect(await screen.findByText(/your account has no access yet/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /sign out/i })).toHaveAttribute('href', '/logout');
    expect(screen.queryByText('workspace')).toBeNull();
  });

  it('signs out from inside the app without asking again', async () => {
    serveSignedIn(false);

    renderGuardedPage();
    fireEvent.click(await screen.findByRole('link', { name: /sign out/i }));

    expect(await screen.findByText(/logout page/i)).toHaveTextContent('"fromApp":true');
  });

  it('lets a user who was just granted a role check again', async () => {
    let canRead = false;
    serveUser(true);
    mswServer.use(http.get('*/beacon/api/auth/permissions', () => HttpResponse.json({ canRead, canWrite: false })));

    renderGuardedPage();
    await screen.findByText(/your account has no access yet/i);
    canRead = true;
    fireEvent.click(screen.getByRole('button', { name: /check again/i }));

    expect(await screen.findByText('workspace')).toBeInTheDocument();
  });

  it('renders the page for a user who can read', async () => {
    serveSignedIn(true);

    renderGuardedPage();

    expect(await screen.findByText('workspace')).toBeInTheDocument();
    expect(screen.queryByText(/no access yet/i)).toBeNull();
  });

  it('renders nothing of the page while the permissions are still loading', async () => {
    serveUser(true);
    mswServer.use(
      http.get('*/beacon/api/auth/permissions', async () => {
        await delay('infinite');
        return HttpResponse.json({ canRead: true, canWrite: true });
      }),
    );

    renderGuardedPage();

    expect(await screen.findByText(/loading/i)).toBeInTheDocument();
    expect(screen.queryByText('workspace')).toBeNull();
  });

  it('renders nothing of the page when the permissions cannot be loaded', async () => {
    serveUser(true);
    mswServer.use(http.get('*/beacon/api/auth/permissions', () => new HttpResponse(null, { status: 500 })));

    renderGuardedPage();

    expect(await screen.findByText(/failed to load your permissions/i)).toBeInTheDocument();
    expect(screen.queryByText('workspace')).toBeNull();
  });
});

describe('RequireAuth for an anonymous user', () => {
  it('redirects to sign-in without asking for permissions', async () => {
    serveUser(false);
    let permissionsRequested = false;
    mswServer.use(
      http.get('*/beacon/api/auth/permissions', () => {
        permissionsRequested = true;
        return HttpResponse.json({ canRead: true, canWrite: true });
      }),
    );

    renderGuardedPage();

    expect(await screen.findByText('login page')).toBeInTheDocument();
    await waitFor(() => expect(permissionsRequested).toBe(false));
  });
});
