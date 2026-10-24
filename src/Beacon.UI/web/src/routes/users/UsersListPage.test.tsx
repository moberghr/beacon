import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { toast } from 'sonner';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import UsersListPage from './UsersListPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

function renderAtUsers() {
  return renderWithProviders(
    <Routes>
      <Route path="/users" element={<UsersListPage />} />
      <Route path="/home" element={<span>home page</span>} />
    </Routes>,
    { initialEntries: ['/users'] },
  );
}

describe('UsersListPage', () => {
  it('opens for a UserAdmin who is not an Admin', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['UserAdmin'] }),
      http.get('*/beacon/api/users/roles', () => HttpResponse.json({ entries: [] })),
      http.get('*/beacon/api/users', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [
            {
              id: 4,
              userName: 'bea',
              email: null,
              displayName: 'Bea',
              isInternalUser: true,
              isSuperAdmin: false,
              isEnabled: true,
              lastLoginAt: null,
              roles: [{ id: 3, name: 'Viewer', level: 1 }],
            },
          ],
        }),
      ),
    );

    renderAtUsers();

    expect(await screen.findByText('bea')).toBeInTheDocument();
  });

  it('sends a holder of every other permission home, saying which one the page needs', async () => {
    // onUnhandledRequest is 'error': listing the users would fail this test.
    mswServer.use(
      permissionsHandler({ permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'DataSourceAdmin', 'MigrationOperator'] }),
    );

    renderAtUsers();

    expect(await screen.findByText('home page')).toBeInTheDocument();
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The UserAdmin permission is required for this page.'));
  });

  const ROW = {
    id: 4,
    userName: 'bea',
    email: null,
    displayName: 'Bea',
    isInternalUser: true,
    isSuperAdmin: false,
    isEnabled: true,
    lastLoginAt: null,
    roles: [{ id: 3, name: 'Viewer', level: 1 }],
  };

  it('marks a user whose Admin role waits for approval, for an Admin', async () => {
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      http.get('*/beacon/api/users/roles', () => HttpResponse.json({ entries: [] })),
      http.get('*/beacon/api/users', () => HttpResponse.json({ totalCount: 1, pageCount: 1, items: [ROW] })),
      http.get('*/beacon/api/approvals/changes', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [{ id: 61, kind: 6, resourceId: 4, resourceName: 'User 4', status: 0, createdTime: '2026-10-23T10:00:00Z' }],
        }),
      ),
    );

    renderAtUsers();

    expect(await screen.findByText('Admin pending approval')).toBeInTheDocument();
  });

  it('reads no Admin role grants and shows no mark for a UserAdmin who is not an Admin', async () => {
    // onUnhandledRequest is 'error': reading the approvals would fail this test.
    mswServer.use(
      permissionsHandler({ permissions: ['UserAdmin'] }),
      http.get('*/beacon/api/users/roles', () => HttpResponse.json({ entries: [] })),
      http.get('*/beacon/api/users', () => HttpResponse.json({ totalCount: 1, pageCount: 1, items: [ROW] })),
    );

    renderAtUsers();

    expect(await screen.findByText('bea')).toBeInTheDocument();
    expect(screen.queryByText('Admin pending approval')).not.toBeInTheDocument();
  });
});
