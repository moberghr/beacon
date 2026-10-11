import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../vitest.setup';
import { FunctionalOnlyGate } from './FunctionalOnlyGate';

function renderAt(path: string) {
  return renderWithProviders(
    <FunctionalOnlyGate>
      <span>page content</span>
    </FunctionalOnlyGate>,
    { initialEntries: [path] },
  );
}

describe('FunctionalOnlyGate', () => {
  it('explains to a user with functional roles only that project data needs a Viewer or Editor role', async () => {
    mswServer.use(functionalOnly('UserAdmin', 'Auditor'));

    renderAt('/home');

    expect(await screen.findByText('Project data needs a Viewer or Editor role')).toBeInTheDocument();
    expect(screen.queryByText('page content')).not.toBeInTheDocument();
    // The pages their permissions open stay reachable.
    expect(screen.getByRole('link', { name: 'User management' })).toHaveAttribute('href', '/users');
    expect(screen.getByRole('link', { name: 'API keys' })).toHaveAttribute('href', '/api-keys');
    expect(screen.getByRole('link', { name: 'MCP settings' })).toHaveAttribute('href', '/mcp-settings');
    expect(screen.queryByRole('link', { name: 'Approvals' })).not.toBeInTheDocument();
  });

  it('shows such a user the pages their permissions open', async () => {
    mswServer.use(functionalOnly('UserAdmin'));

    renderAt('/users');

    expect(await screen.findByText('page content')).toBeInTheDocument();
  });

  it('shows the notice on a page of a permission the user does not hold', async () => {
    mswServer.use(functionalOnly('Approver'));

    renderAt('/users');

    expect(await screen.findByText('Project data needs a Viewer or Editor role')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Approvals' })).toBeInTheDocument();
  });

  it('shows every page to a user who reads project data', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));

    renderAt('/queries');

    expect(await screen.findByText('page content')).toBeInTheDocument();
  });
});
