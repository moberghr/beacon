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
  toast: { success: vi.fn(), error: vi.fn() },
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
});
