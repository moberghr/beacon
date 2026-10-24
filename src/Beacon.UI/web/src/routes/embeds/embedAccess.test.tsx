import { describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AccessReady } from '@/test/AccessReady';
import { permissionsHandler, type PermissionsFixture } from '@/test/permissions';
import { accessOf, pagesOpenedBy } from '@/auth/access';
import { RequireEmbed } from '@/auth/RequireEmbed';
import { Sidebar } from '@/components/layout/Sidebar';

function renderAt(path: string) {
  return renderWithProviders(
    <Routes>
      <Route path="/home" element={<span>home page</span>} />
      <Route element={<RequireEmbed />}>
        <Route path="/embeds" element={<span>embeds page</span>} />
      </Route>
    </Routes>,
    { initialEntries: [path] },
  );
}

const securityAdmin: PermissionsFixture = { permissions: ['SecurityAdmin'], embedEnabled: true };

describe('the embeds page and its navigation', () => {
  it('open to a SecurityAdmin while the deployment serves embeds', async () => {
    mswServer.use(permissionsHandler(securityAdmin));

    renderAt('/embeds');

    expect(await screen.findByText('embeds page')).toBeInTheDocument();
  });

  it('open to an Admin while the deployment serves embeds', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, embedEnabled: true }));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Embeds')).toBeInTheDocument();
  });

  it.each([
    ['the deployment does not serve embeds', { isAdmin: true, embedEnabled: false }],
    ['the caller is not a SecurityAdmin', { permissions: ['Auditor', 'Approver'], embedEnabled: true }],
  ] as [string, PermissionsFixture][])('lead a direct link home while %s', async (_, fixture) => {
    mswServer.use(permissionsHandler(fixture));

    renderAt('/embeds');

    expect(await screen.findByText('home page')).toBeInTheDocument();
    expect(screen.queryByText('embeds page')).not.toBeInTheDocument();
  });

  it.each([
    ['the deployment does not serve embeds', { isAdmin: true, embedEnabled: false }],
    ['the caller is not a SecurityAdmin', { permissions: ['Auditor', 'Approver'], embedEnabled: true }],
  ] as [string, PermissionsFixture][])('hide the sidebar entry while %s', async (_, fixture) => {
    mswServer.use(permissionsHandler(fixture));

    renderWithProviders(
      <>
        <Sidebar />
        <AccessReady />
      </>,
    );

    await screen.findByTestId('access-ready');
    expect(await screen.findByText('Queries')).toBeInTheDocument();
    expect(screen.queryByText('Embeds')).not.toBeInTheDocument();
  });

  it('show the sidebar entry to a SecurityAdmin', async () => {
    mswServer.use(permissionsHandler(securityAdmin));

    renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Embeds')).toBeInTheDocument();
  });

  it('open the page to a SecurityAdmin without project roles only while embeds are served', () => {
    const on = accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: ['SecurityAdmin'], embedEnabled: true }, false);
    const off = accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: ['SecurityAdmin'] }, false);

    expect(pagesOpenedBy(on).map(x => x.path)).toEqual(['/mcp-settings', '/admin-settings', '/embeds']);
    expect(on.opensEmbeds).toBe(true);
    expect(pagesOpenedBy(off).map(x => x.path)).toEqual(['/mcp-settings', '/admin-settings']);
    expect(off.opensEmbeds).toBe(false);
  });
});
