import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { Sidebar } from './Sidebar';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsForRoles, permissionsHandler } from '@/test/permissions';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/** The pages a functional permission opens, by sidebar name. */
const PERMISSION_PAGES = ['Data Migration', 'MCP Settings', 'Approvals', 'User Management', 'Admin Settings', 'Security Audit', 'MCP Audit'];

function sessionOf(isAuthenticated: boolean, roles: string[], displayName: string, signOutEverywhereEnabled = true) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: isAuthenticated ? 'u1' : null,
        displayName,
        email: null,
        isAuthenticated,
        roles,
        realtimeEnabled: false,
        externalLogin: null,
        signOutEverywhereEnabled,
      }),
    ),
  );
}

function signedInAs(roles: string[], signOutEverywhereEnabled = true) {
  sessionOf(true, roles, 'Tester', signOutEverywhereEnabled);
  mswServer.use(permissionsForRoles(roles));
}

describe('Sidebar', () => {
  it('shows the running build the server reports to the signed-in session', async () => {
    sessionOf(true, ['Viewer'], 'Tester');
    mswServer.use(permissionsHandler({ build: { version: '5.0.1', commit: '0ecf936b094ad56483d71341b37b0e7878113a3e' } }));

    renderWithProviders(<Sidebar />);

    const badge = await screen.findByText('5.0.1 · 0ecf936');
    expect(badge).toHaveAttribute('title', 'Build 5.0.1+0ecf936b094ad56483d71341b37b0e7878113a3e');
  });

  it('shows no build when the server reports none', async () => {
    signedInAs(['Viewer']);

    const { container } = renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Projects')).toBeInTheDocument();
    expect(container.querySelector('[title^="Build "]')).toBeNull();
  });

  it('shows only the pages the MCP gateway serves in gateway mode', async () => {
    sessionOf(true, ['Admin'], 'Tester');
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('MCP Settings')).toBeInTheDocument();
    expect(screen.getByText('Projects')).toBeInTheDocument();
    expect(screen.getByText('MCP Learning')).toBeInTheDocument();
    expect(screen.getByText('MCP Audit')).toBeInTheDocument();
    for (const name of ['Home', 'Control Tower', 'Data Sources', 'Queries', 'API Keys', 'MCP Playground', 'Approvals', 'User Management', 'Security Audit']) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it('offers no Settings in gateway mode, whose server serves no user settings', async () => {
    sessionOf(true, ['Admin'], 'Tester');
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));

    renderWithProviders(<Sidebar />);
    await screen.findByText('MCP Settings');
    fireEvent.click(screen.getByText('Tester'));

    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Settings' })).not.toBeInTheDocument();
  });

  it('opens no user menu in gateway mode when the host also owns sign-out', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({
          userId: 'u1',
          displayName: 'Tester',
          email: null,
          isAuthenticated: true,
          roles: ['Admin'],
          realtimeEnabled: false,
          externalLogin: { loginUrl: '/account/login', returnUrlParameter: 'returnUrl' },
        }),
      ),
      permissionsHandler({ isAdmin: true, gatewayMode: true }),
    );

    renderWithProviders(<Sidebar />);
    await screen.findByText('MCP Settings');
    const trigger = screen.getByRole('button', { name: /Tester/ });
    fireEvent.click(trigger);

    expect(trigger).not.toHaveAttribute('aria-haspopup');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('keeps the full navigation without gateway mode', async () => {
    sessionOf(true, ['Admin'], 'Tester');
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: false }));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Control Tower')).toBeInTheDocument();
    expect(screen.getByText('MCP Playground')).toBeInTheDocument();
  });

  it('hides the pages a functional permission opens from a user who holds none', async () => {
    signedInAs(['Editor']);

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Tester')).toBeInTheDocument();
    expect(screen.getByText('Queries')).toBeInTheDocument();
    for (const name of PERMISSION_PAGES) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it('shows those pages to Admins', async () => {
    signedInAs(['Admin']);

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('User Management')).toBeInTheDocument();
    for (const name of PERMISSION_PAGES) {
      expect(screen.getByText(name)).toBeInTheDocument();
    }
  });

  it.each([
    ['Approver', ['Approvals']],
    ['Auditor', ['MCP Settings', 'Admin Settings', 'Security Audit', 'MCP Audit']],
    ['SecurityAdmin', ['MCP Settings', 'Admin Settings']],
    ['UserAdmin', ['User Management']],
    ['MigrationOperator', ['Data Migration']],
  ] as const)('shows a %s the pages that permission opens, and no other', async (permission, pages) => {
    signedInAs(['Editor']);
    mswServer.use(permissionsHandler({ permissions: [permission] }));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText(pages[0])).toBeInTheDocument();
    for (const name of PERMISSION_PAGES) {
      if ((pages as readonly string[]).includes(name)) {
        expect(screen.getByText(name)).toBeInTheDocument();
      } else {
        expect(screen.queryByText(name)).not.toBeInTheDocument();
      }
    }
    // A Viewer or Editor role still reads project data.
    expect(screen.getByText('Queries')).toBeInTheDocument();
  });

  it('shows no Data Migration entry, to an Admin either, while the deployment does not run migrations', async () => {
    signedInAs(['Admin']);
    mswServer.use(permissionsHandler({ isAdmin: true, migrationsEnabled: false }));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('User Management')).toBeInTheDocument();
    expect(screen.queryByText('Data Migration')).not.toBeInTheDocument();
  });

  it('shows a user with functional roles only the pages those open, not the project-data pages', async () => {
    sessionOf(true, ['Auditor', 'DataSourceAdmin'], 'Tester');
    mswServer.use(functionalOnly('Auditor', 'DataSourceAdmin'));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('MCP Settings')).toBeInTheDocument();
    expect(screen.getByText('Admin Settings')).toBeInTheDocument();
    expect(screen.getByText('Security Audit')).toBeInTheDocument();
    expect(screen.getByText('API Keys')).toBeInTheDocument();
    expect(screen.getByText('Data Sources')).toBeInTheDocument();
    expect(screen.getByText('Home')).toBeInTheDocument();
    for (const name of ['Queries', 'Projects', 'Control Tower', 'Subscriptions', 'Recipients', 'MCP Playground']) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
    expect(screen.queryByText('User Management')).not.toBeInTheDocument();
  });

  it.each([
    ['Auditor', true],
    ['UserAdmin', true],
    ['SecurityAdmin', false],
    ['Approver', false],
  ] as const)('shows a %s with functional roles only the API Keys entry: %s', async (permission, shown) => {
    sessionOf(true, [permission], 'Tester');
    mswServer.use(functionalOnly(permission));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Home')).toBeInTheDocument();
    await screen.findAllByText(/Settings|Approvals|User Management/);
    if (shown) {
      expect(screen.getByText('API Keys')).toBeInTheDocument();
    } else {
      expect(screen.queryByText('API Keys')).not.toBeInTheDocument();
    }
  });

  it('shows no permission-gated page while the session is still loading', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/me', async () => {
        await delay('infinite');
        return HttpResponse.json({});
      }),
    );

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Queries')).toBeInTheDocument();
    for (const name of PERMISSION_PAGES) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it('shows no permission-gated page to a session that is not signed in, whatever roles it lists', async () => {
    sessionOf(false, ['Admin'], 'Visitor');
    mswServer.use(permissionsForRoles(['Admin']));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Visitor')).toBeInTheDocument();
    for (const name of PERMISSION_PAGES) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it.each([
    ['Sign out', null],
    ['Sign out everywhere', 'true'],
  ])('"%s" asks the server to end this session, and every other one only when everywhere', async (item, everywhere) => {
    signedInAs(['Editor']);
    const logouts: (string | null)[] = [];
    mswServer.use(
      http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
      http.post('*/beacon/api/auth/logout', ({ request }) => {
        logouts.push(new URL(request.url).searchParams.get('everywhere'));
        return HttpResponse.json({ success: true });
      }),
    );

    renderWithProviders(<Sidebar />);
    fireEvent.click(await screen.findByText('Tester'));
    fireEvent.click(screen.getByRole('menuitem', { name: item }));

    await waitFor(() => expect(logouts).toEqual([everywhere]));
  });

  it('offers "Sign out everywhere" only where the server says the session can end the others', async () => {
    signedInAs(['Editor'], false);

    renderWithProviders(<Sidebar />);
    fireEvent.click(await screen.findByText('Tester'));

    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Sign out everywhere' })).not.toBeInTheDocument();
  });

  it('says so, and stays signed in, when the other sessions could not be signed out', async () => {
    signedInAs(['Editor']);
    vi.mocked(toast.error).mockClear();
    mswServer.use(
      http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
      http.post('*/beacon/api/auth/logout', () =>
        HttpResponse.json({ title: 'Conflict', status: 409, detail: 'Sessions are not checked against Beacon\'s user store.' }, { status: 409 }),
      ),
    );

    renderWithProviders(<Sidebar />);
    fireEvent.click(await screen.findByText('Tester'));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Sign out everywhere' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('could not be signed out')));
    expect(screen.getByRole('menuitem', { name: 'Sign out everywhere' })).not.toBeDisabled();
  });
  it('offers neither sign-out to a user of a host that owns sign-in, even where the server could end the other sessions', async () => {
    signedInAs(['Editor']);
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({
          userId: 'u1',
          displayName: 'Tester',
          email: null,
          isAuthenticated: true,
          roles: ['Editor'],
          realtimeEnabled: false,
          externalLogin: { loginUrl: '/account/login', returnUrlParameter: 'returnUrl' },
          signOutEverywhereEnabled: true,
        }),
      ),
    );

    renderWithProviders(<Sidebar />);
    fireEvent.click(await screen.findByText('Tester'));

    expect(screen.getByRole('menuitem', { name: 'Settings' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Sign out' })).not.toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Sign out everywhere' })).not.toBeInTheDocument();
  });

  it('badges AI Actors with the plans waiting for the user, and shows no badge while none waits', async () => {
    signedInAs(['Editor']);
    mswServer.use(http.get('*/beacon/api/ai-actors/pending-plans/count', () => HttpResponse.json({ count: 2 })));
    const { unmount } = renderWithProviders(<Sidebar />);

    expect(await screen.findByLabelText('2 AI actor plans pending in the projects you may decide for')).toHaveTextContent('2');
    unmount();

    mswServer.use(http.get('*/beacon/api/ai-actors/pending-plans/count', () => HttpResponse.json({ count: 0 })));
    renderWithProviders(<Sidebar />);
    expect(await screen.findByText('AI Actors')).toBeInTheDocument();
    expect(screen.queryByLabelText(/AI actor plans? pending/)).not.toBeInTheDocument();
  });

  it('does not ask for the pending plans in gateway mode', async () => {
    const asked = vi.fn();
    sessionOf(true, ['Admin'], 'Tester');
    mswServer.use(
      permissionsHandler({ isAdmin: true, gatewayMode: true }),
      http.get('*/beacon/api/ai-actors/pending-plans/count', () => {
        asked();
        return HttpResponse.json({ count: 3 });
      }),
    );

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('MCP Settings')).toBeInTheDocument();
    await new Promise(resolve => setTimeout(resolve, 50));
    expect(asked).not.toHaveBeenCalled();
  });
});
