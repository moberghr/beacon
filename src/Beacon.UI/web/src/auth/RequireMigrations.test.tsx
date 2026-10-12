import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Route, Routes } from 'react-router-dom';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../vitest.setup';
import { RequireMigrations } from './RequireMigrations';

function renderAt(path: string) {
  return renderWithProviders(
    <Routes>
      <Route path="/home" element={<span>home page</span>} />
      <Route element={<RequireMigrations />}>
        <Route path="/migration-jobs" element={<span>migration jobs</span>} />
      </Route>
    </Routes>,
    { initialEntries: [path] },
  );
}

describe('RequireMigrations', () => {
  it('opens a migration page while the deployment runs migrations', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, migrationsEnabled: true }));

    renderAt('/migration-jobs');

    expect(await screen.findByText('migration jobs')).toBeInTheDocument();
  });

  it('leads a direct link home while the deployment does not run migrations', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, migrationsEnabled: false }));

    renderAt('/migration-jobs');

    expect(await screen.findByText('home page')).toBeInTheDocument();
    expect(screen.queryByText('migration jobs')).not.toBeInTheDocument();
  });
});
