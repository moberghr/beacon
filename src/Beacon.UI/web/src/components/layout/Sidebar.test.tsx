import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { Sidebar } from './Sidebar';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsForRoles, permissionsHandler } from '@/test/permissions';

/** The pages a functional permission opens, by sidebar name. */
const PERMISSION_PAGES = ['Data Migration', 'MCP Settings', 'Approvals', 'User Management', 'Admin Settings'];

function sessionOf(isAuthenticated: boolean, roles: string[], displayName: string) {
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
      }),
    ),
  );
}

function signedInAs(roles: string[]) {
  sessionOf(true, roles, 'Tester');
  mswServer.use(permissionsForRoles(roles));
}

describe('Sidebar', () => {
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
    ['Auditor', ['MCP Settings', 'Admin Settings']],
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

  it('shows a user with functional roles only the pages those open, not the project-data pages', async () => {
    sessionOf(true, ['Auditor', 'DataSourceAdmin'], 'Tester');
    mswServer.use(functionalOnly('Auditor', 'DataSourceAdmin'));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('MCP Settings')).toBeInTheDocument();
    expect(screen.getByText('Admin Settings')).toBeInTheDocument();
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
});
