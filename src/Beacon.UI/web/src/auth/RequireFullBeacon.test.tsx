import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router-dom';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../vitest.setup';
import { RequireFullBeacon } from './RequireFullBeacon';

function renderAt(path: string) {
  return renderWithProviders(
    <Routes>
      <Route path="/mcp-audit" element={<span>gateway landing</span>} />
      <Route element={<RequireFullBeacon />}>
        <Route path="/queries" element={<span>queries page</span>} />
      </Route>
    </Routes>,
    { initialEntries: [path] },
  );
}

describe('RequireFullBeacon', () => {
  it('opens the page without gateway mode', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: false }));

    renderAt('/queries');

    expect(await screen.findByText('queries page')).toBeInTheDocument();
  });

  it('leads a direct link to the gateway landing page in gateway mode', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));

    renderAt('/queries');

    expect(await screen.findByText('gateway landing')).toBeInTheDocument();
    expect(screen.queryByText('queries page')).not.toBeInTheDocument();
  });
});
