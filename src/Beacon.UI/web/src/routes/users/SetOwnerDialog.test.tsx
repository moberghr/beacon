import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { permissionsForRoles, permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import { mswServer } from '../../../vitest.setup';
import { SELF_OWNER_NOTE, SetOwnerDialog } from './SetOwnerDialog';

// The signed-in user is 'tester' (the default session).
const users = http.get('*/beacon/api/users', () =>
  HttpResponse.json({
    items: [
      { id: 1, userName: 'tester', displayName: 'Tester', email: null },
      { id: 2, userName: 'bea', displayName: 'Bea Analyst', email: null },
    ],
    totalCount: 2,
    pageCount: 1,
  }),
);

async function pick(name: string) {
  const dialog = screen.getByRole('dialog');
  fireEvent.click(within(dialog).getByRole('button', { name: 'New owner' }));
  fireEvent.click(await within(dialog).findByRole('option', { name: new RegExp(name) }));
}

function renderDialog(onSubmit = vi.fn()) {
  renderWithProviders(
    <>
      <SetOwnerDialog open noun="AI actor" busy={false} onClose={vi.fn()} onSubmit={onSubmit} />
      <AccessReady />
    </>,
  );
}

describe('SetOwnerDialog', () => {
  it('says what the owner means for the resource when the page says so', () => {
    renderWithProviders(
      <SetOwnerDialog
        open
        noun="data contract"
        description="Scheduled evaluations run as the owner."
        busy={false}
        onClose={vi.fn()}
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByRole('dialog', { name: "Change the data contract's owner" })).toBeInTheDocument();
    expect(screen.getByText('Scheduled evaluations run as the owner.')).toBeInTheDocument();
    expect(screen.queryByText(/may change, run and delete/)).not.toBeInTheDocument();
  });

  it('falls back to who may change, run and delete the resource', () => {
    renderWithProviders(<SetOwnerDialog open noun="AI actor" busy={false} onClose={vi.fn()} onSubmit={vi.fn()} />);

    expect(screen.getByText('The owner may change, run and delete this AI actor. Admins always can.')).toBeInTheDocument();
  });

  it('does not let a UserAdmin who is not an Admin name themselves, and says why', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }), users);
    const onSubmit = vi.fn();
    renderDialog(onSubmit);
    await screen.findByTestId('access-ready');

    await pick('Tester');
    expect(screen.getByText(SELF_OWNER_NOTE)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save owner' })).toBeDisabled();

    await pick('Bea Analyst');
    expect(screen.queryByText(SELF_OWNER_NOTE)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save owner' }));
    expect(onSubmit).toHaveBeenCalledWith(2);
  });

  it('lets an Admin name themselves', async () => {
    mswServer.use(permissionsForRoles(['Admin']), users);
    const onSubmit = vi.fn();
    renderDialog(onSubmit);
    await screen.findByTestId('access-ready');

    await pick('Tester');
    expect(screen.queryByText(SELF_OWNER_NOTE)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save owner' }));
    expect(onSubmit).toHaveBeenCalledWith(1);
  });
});
