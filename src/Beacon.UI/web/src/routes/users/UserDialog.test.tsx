import { describe, it, expect, vi, beforeEach } from 'vitest';
import { delay, http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsForRoles, permissionsHandler } from '@/test/permissions';
import { UserDialog } from './UserDialog';
import type { UserEntry } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

const ROLES = [
  { id: 1, name: 'Admin', description: null, level: 3, isSystemRole: true, permission: null },
  { id: 2, name: 'Editor', description: null, level: 2, isSystemRole: true, permission: null },
  { id: 3, name: 'Viewer', description: null, level: 1, isSystemRole: true, permission: null },
  { id: 10, name: 'Auditor', description: null, level: 0, isSystemRole: true, permission: 'Auditor' },
  { id: 11, name: 'SecurityAdmin', description: null, level: 0, isSystemRole: true, permission: 'SecurityAdmin' },
];

function user(overrides: Partial<UserEntry> = {}): UserEntry {
  return {
    id: 7,
    userName: 'bea',
    email: null,
    displayName: 'Bea',
    isInternalUser: true,
    isSuperAdmin: false,
    isEnabled: true,
    lastLoginAt: null,
    roles: [{ id: 3, name: 'Viewer', level: 1 }],
    ...overrides,
  };
}

/** Records the account and role writes; `rolesReply` answers the role write. */
function serve(rolesReply: () => Response = () => new HttpResponse(null, { status: 204 })) {
  const writes = { account: 0, roles: [] as unknown[] };
  mswServer.use(
    http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
    http.get('*/beacon/api/users/roles', () => HttpResponse.json({ entries: ROLES })),
    http.put('*/beacon/api/users/7', () => {
      writes.account += 1;
      return new HttpResponse(null, { status: 204 });
    }),
    http.put('*/beacon/api/users/7/roles', async ({ request }) => {
      writes.roles.push(await request.json());
      return rolesReply();
    }),
  );
  return writes;
}

function signedInAs(userName: string) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: 'ext-1',
        userName,
        displayName: userName,
        email: null,
        isAuthenticated: true,
        roles: [],
        realtimeEnabled: false,
        externalLogin: null,
      }),
    ),
  );
}

const problem = (status: number, detail: string) =>
  HttpResponse.json({ title: 'Refused', status, detail }, { status, headers: { 'Content-Type': 'application/problem+json' } });

function renderEdit(target: UserEntry, onClose = () => {}) {
  renderWithProviders(<UserDialog open user={target} onClose={onClose} />);
  return screen.findByRole('checkbox', { name: 'Auditor' });
}

describe('UserDialog roles', () => {
  beforeEach(() => {
    signedInAs('ada');
  });

  it('lets an Admin assign a functional role, listed apart, and saves only the roles', async () => {
    mswServer.use(permissionsForRoles(['Admin']));
    const writes = serve();
    const onClose = vi.fn();

    const auditor = await renderEdit(user(), onClose);
    expect(within(screen.getByRole('group', { name: 'Functional roles' })).getByRole('checkbox', { name: 'Auditor' }))
      .toBe(auditor);
    expect(within(screen.getByRole('group', { name: 'Global roles' })).getByRole('checkbox', { name: 'Viewer' }))
      .toBeChecked();
    await waitFor(() => expect(auditor).toBeEnabled());
    expect(screen.getByRole('checkbox', { name: 'Admin' })).toBeEnabled();

    fireEvent.click(auditor);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(writes.roles).toEqual([{ roleIds: [3, 10] }]);
    expect(writes.account).toBe(0);
  });

  it('shows a UserAdmin the Admin and functional roles disabled, and the others assignable', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve();

    const auditor = await renderEdit(user());

    expect(await screen.findByText(/only an admin assigns or removes the admin role and the functional roles/i))
      .toBeInTheDocument();
    expect(auditor).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'SecurityAdmin' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Admin' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Editor' })).toBeEnabled();
  });

  it('keeps the dialog open with the reason when separation of duties refuses the roles (400)', async () => {
    mswServer.use(permissionsForRoles(['Admin']));
    serve(() => problem(400, 'Separation of duties: one user cannot hold both Auditor and SecurityAdmin.'));
    const onClose = vi.fn();

    const auditor = await renderEdit(user({ roles: [{ id: 11, name: 'SecurityAdmin', level: 0 }] }), onClose);
    await waitFor(() => expect(auditor).toBeEnabled());
    fireEvent.click(auditor);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('The roles were not saved')).toBeInTheDocument();
    expect(screen.getByText(/one user cannot hold both Auditor and SecurityAdmin/)).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('says why when the server refuses the role change (403)', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve(() => problem(403, 'Only an Admin can assign or remove the Admin role or a functional role.'));
    const onClose = vi.fn();

    await renderEdit(user(), onClose);
    const editor = screen.getByRole('checkbox', { name: 'Editor' });
    await waitFor(() => expect(editor).toBeEnabled());
    fireEvent.click(editor);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('You may not make this role change')).toBeInTheDocument();
    expect(screen.getByText(/only an admin can assign or remove the admin role or a functional role/i)).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('shows a UserAdmin their own account read-only, with a note', async () => {
    signedInAs('bea');
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    const writes = serve();

    await renderEdit(user());

    expect(await screen.findByText('You cannot change your own account or roles; an Admin can.')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Editor' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    expect(writes.roles).toEqual([]);
  });

  it('shows a UserAdmin a functional role holder read-only', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve();

    await renderEdit(user({ roles: [{ id: 10, name: 'Auditor', level: 0 }] }));

    expect(await screen.findByText(/only an admin can change the account of an admin, a super admin or a holder of a functional role/i))
      .toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  });

  it('lets an Admin change their own roles', async () => {
    signedInAs('bea');
    mswServer.use(permissionsForRoles(['Admin']));
    serve();

    const auditor = await renderEdit(user());

    await waitFor(() => expect(auditor).toBeEnabled());
    expect(screen.queryByText(/you cannot change your own account/i)).not.toBeInTheDocument();
  });

  it('offers a UserAdmin creating a user neither the Admin role nor a functional role', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve();

    renderWithProviders(<UserDialog open user={null} onClose={() => {}} />);

    const auditor = await screen.findByRole('checkbox', { name: 'Auditor' });
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Viewer' })).toBeEnabled());
    expect(auditor).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Admin' })).toBeDisabled();
  });
});

describe('UserDialog roles query', () => {
  beforeEach(() => {
    signedInAs('ada');
    mswServer.use(permissionsForRoles(['Admin']));
  });

  it('says the roles are loading while they load, not that there are none', async () => {
    mswServer.use(
      http.get('*/beacon/api/users/roles', async () => {
        await delay('infinite');
        return HttpResponse.json({ entries: ROLES });
      }),
    );

    renderWithProviders(<UserDialog open user={null} onClose={() => {}} />);

    expect(await screen.findByText('Loading roles…')).toBeInTheDocument();
    expect(screen.queryByText('No roles available.')).not.toBeInTheDocument();
  });

  it('says the roles could not be loaded, and loads them on retry', async () => {
    let calls = 0;
    mswServer.use(
      http.get('*/beacon/api/users/roles', () => {
        calls += 1;
        return calls === 1
          ? HttpResponse.json({ title: 'Failed', status: 500 }, { status: 500 })
          : HttpResponse.json({ entries: ROLES });
      }),
    );

    renderWithProviders(<UserDialog open user={user()} onClose={() => {}} />);

    expect(await screen.findByText('The roles could not be loaded.')).toBeInTheDocument();
    expect(screen.queryByText('No roles available.')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('checkbox', { name: 'Auditor' })).toBeInTheDocument();
  });

  it('says there are no roles only when the server lists none', async () => {
    mswServer.use(http.get('*/beacon/api/users/roles', () => HttpResponse.json({ entries: [] })));

    renderWithProviders(<UserDialog open user={null} onClose={() => {}} />);

    expect(await screen.findByText('No roles available.')).toBeInTheDocument();
  });
});
