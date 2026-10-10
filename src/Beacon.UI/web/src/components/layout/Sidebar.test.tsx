import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { Sidebar } from './Sidebar';
import { renderWithProviders } from '@/test/render';

const ADMIN_ONLY = ['Data Migration', 'MCP Settings', 'User Management'];

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
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: 'u1',
        displayName: 'Tester',
        email: null,
        isAuthenticated: true,
        roles,
        realtimeEnabled: false,
        externalLogin: null,
      }),
    ),
  );
}

describe('Sidebar', () => {
  it('hides the pages whose data only Admins can read from other users', async () => {
    signedInAs(['Editor']);

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Tester')).toBeInTheDocument();
    expect(screen.getByText('Queries')).toBeInTheDocument();
    for (const name of ADMIN_ONLY) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it('shows those pages to Admins', async () => {
    signedInAs(['Admin']);

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('User Management')).toBeInTheDocument();
    for (const name of ADMIN_ONLY) {
      expect(screen.getByText(name)).toBeInTheDocument();
    }
  });

  it('shows no Admin-only page while the session is still loading', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/me', async () => {
        await delay('infinite');
        return HttpResponse.json({});
      }),
    );

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Queries')).toBeInTheDocument();
    expect(screen.getByText('Admin Settings')).toBeInTheDocument();
    for (const name of ADMIN_ONLY) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it('shows no Admin-only page to a session that is not signed in, whatever roles it lists', async () => {
    sessionOf(false, ['Admin'], 'Visitor');

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Visitor')).toBeInTheDocument();
    expect(screen.getByText('Admin Settings')).toBeInTheDocument();
    for (const name of ADMIN_ONLY) {
      expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });
});
