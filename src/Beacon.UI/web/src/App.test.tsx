import { type ReactNode } from 'react';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../vitest.setup';
import App from './App';

// The routing under test is App's own: the shell, the sign-in check and the pages are stand-ins.
vi.mock('./auth/RequireAuth', () => ({ RequireAuth: ({ children }: { children: ReactNode }) => children }));
vi.mock('./components/layout/AppShell', async () => {
  const { Outlet } = await import('react-router-dom');
  return { AppShell: () => <Outlet /> };
});
vi.mock('./routes/home/HomePage', () => ({ default: () => <span>home page</span> }));
vi.mock('./routes/migration-jobs/MigrationJobsListPage', () => ({ default: () => <span>migration jobs page</span> }));
vi.mock('./routes/migration-jobs/NewMigrationJobPage', () => ({ default: () => <span>new migration job page</span> }));
vi.mock('./routes/migration-jobs/MigrationJobDetailPage', () => ({ default: () => <span>migration job page</span> }));
vi.mock('./routes/migration-history/MigrationHistoryPage', () => ({ default: () => <span>migration history page</span> }));
vi.mock('./routes/settings/SettingsPage', () => ({ default: () => <span>user settings page</span> }));
vi.mock('./routes/mcp/McpAuditPage', () => ({ default: () => <span>mcp audit page</span> }));

function renderAppAt(path: string) {
  window.history.pushState({}, '', `/beacon${path}`);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    <QueryClientProvider client={client}>
      <App />
    </QueryClientProvider>,
  );
}

const MIGRATION_PAGES: [string, string][] = [
  ['/migration-jobs', 'migration jobs page'],
  ['/migration-jobs/new', 'new migration job page'],
  ['/migration-jobs/7', 'migration job page'],
  ['/migration-history', 'migration history page'],
];

describe('App routes', () => {
  afterEach(() => window.history.pushState({}, '', '/'));

  it.each(MIGRATION_PAGES)('opens %s while the deployment runs migrations', async (path, page) => {
    mswServer.use(permissionsHandler({ isAdmin: true, migrationsEnabled: true }));

    renderAppAt(path);

    expect(await screen.findByText(page)).toBeInTheDocument();
  });

  it.each(MIGRATION_PAGES)('leads %s home while the deployment does not run migrations', async (path, page) => {
    mswServer.use(permissionsHandler({ isAdmin: true, migrationsEnabled: false }));

    renderAppAt(path);

    expect(await screen.findByText('home page')).toBeInTheDocument();
    expect(screen.queryByText(page)).not.toBeInTheDocument();
    expect(window.location.pathname).toBe('/beacon/home');
  });
  it('opens the user settings in the full Beacon', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: false }));

    renderAppAt('/settings');

    expect(await screen.findByText('user settings page')).toBeInTheDocument();
  });

  it('leads the user settings to the gateway landing page in gateway mode, whose server serves none', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));

    renderAppAt('/settings');

    expect(await screen.findByText('mcp audit page')).toBeInTheDocument();
    expect(screen.queryByText('user settings page')).not.toBeInTheDocument();
    expect(window.location.pathname).toBe('/beacon/mcp-audit');
  });
});
