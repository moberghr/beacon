import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router-dom';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import { AppShell } from './AppShell';

vi.mock('./Sidebar', () => ({ Sidebar: () => null }));
vi.mock('@/lib/useHubInvalidations', () => ({ useHubInvalidations: () => {} }));

function renderAt(path: string) {
  return renderWithProviders(
    <Routes>
      <Route element={<AppShell />}>
        <Route path="*" element={<span>page content</span>} />
      </Route>
    </Routes>,
    { initialEntries: [path] },
  );
}

describe('AppShell', () => {
  it('shows a user with functional roles only the notice on a page of project data', async () => {
    mswServer.use(functionalOnly('Auditor'));

    renderAt('/queries');

    expect(await screen.findByText('Project data needs a Viewer or Editor role')).toBeInTheDocument();
    expect(screen.queryByText('page content')).not.toBeInTheDocument();
  });

  it('shows such a user the pages their permission opens, and a reader every page', async () => {
    mswServer.use(functionalOnly('Auditor'));
    const { unmount } = renderAt('/api-keys');
    expect(await screen.findByText('page content')).toBeInTheDocument();
    unmount();

    mswServer.use(permissionsHandler());
    renderAt('/queries');
    expect(await screen.findByText('page content')).toBeInTheDocument();
  });
});
