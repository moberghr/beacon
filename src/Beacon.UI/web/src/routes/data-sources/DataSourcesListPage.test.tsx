import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import DataSourcesListPage from './DataSourcesListPage';

const source = (overrides: Record<string, unknown>) => ({
  dataSourceType: 'Database',
  databaseEngineType: 'PostgreSQL',
  queryCount: 0,
  migrationJobsCount: 0,
  metadataLoadingEnabled: true,
  openWhenUnlinked: true,
  projects: [],
  otherProjectCount: 0,
  ...overrides,
});

describe('DataSourcesListPage project chips', () => {
  it('names the readable projects and counts the others without naming them', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          items: [
            source({ id: 1, name: 'ledger-db', projects: [{ id: 5, name: 'Ledger' }], otherProjectCount: 2 }),
            source({ id: 2, name: 'scratch-db', openWhenUnlinked: false }),
            source({ id: 3, name: 'legacy-db' }),
          ],
          totalCount: 3,
          pageCount: 1,
        }),
      ),
    );

    renderWithProviders(<DataSourcesListPage />);

    const ledger = (await screen.findByText('ledger-db')).closest('[role="row"]') as HTMLElement;
    expect(within(ledger).getByText('Ledger')).toBeInTheDocument();
    expect(within(ledger).getByText('+2 other')).toBeInTheDocument();

    // A new source in no project is Admin-only until linked; an older one stays reachable.
    const scratch = screen.getByText('scratch-db').closest('[role="row"]') as HTMLElement;
    expect(within(scratch).getByText(/admins only/i)).toBeInTheDocument();
    const legacy = screen.getByText('legacy-db').closest('[role="row"]') as HTMLElement;
    expect(within(legacy).getByText('Not linked')).toBeInTheDocument();
  });
});

describe('DataSourcesListPage read-only enforcement', () => {
  it('marks only the sources whose engine does not refuse writes', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          items: [
            source({ id: 1, name: 'ledger-db', databaseReadOnlyEnforced: true }),
            source({ id: 2, name: 'crm-db', databaseEngineType: 'MSSQL', databaseReadOnlyEnforced: false }),
            source({ id: 3, name: 'logs', dataSourceType: 'CloudWatch', databaseEngineType: null, databaseReadOnlyEnforced: null }),
          ],
          totalCount: 3,
          pageCount: 1,
        }),
      ),
    );

    renderWithProviders(<DataSourcesListPage />);

    const crm = (await screen.findByText('crm-db')).closest('[role="row"]') as HTMLElement;
    expect(within(crm).getByText('Read-only login required')).toHaveAttribute('title', expect.stringMatching(/login that can only read/));
    const ledger = screen.getByText('ledger-db').closest('[role="row"]') as HTMLElement;
    expect(within(ledger).queryByText('Read-only login required')).not.toBeInTheDocument();
    const logs = screen.getByText('logs').closest('[role="row"]') as HTMLElement;
    expect(within(logs).queryByText('Read-only login required')).not.toBeInTheDocument();
  });

  it('labels a migration write target as one instead of asking for a read-only login', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          items: [
            source({ id: 4, name: 'reporting-db', databaseEngineType: 'MSSQL', databaseReadOnlyEnforced: false, allowMigrationWrites: true }),
          ],
          totalCount: 1,
          pageCount: 1,
        }),
      ),
    );

    renderWithProviders(<DataSourcesListPage />);

    const reporting = (await screen.findByText('reporting-db')).closest('[role="row"]') as HTMLElement;
    expect(within(reporting).getByText('Migration write target')).toHaveAttribute('title', expect.stringMatching(/never reads from it/));
    expect(within(reporting).queryByText('Read-only login required')).not.toBeInTheDocument();
  });
});

describe('DataSourcesListPage and the DataSourceAdmin permission', () => {
  const oneSource = http.get('*/beacon/api/data-sources', () =>
    HttpResponse.json({ items: [source({ id: 1, name: 'ledger-db' })], totalCount: 1, pageCount: 1 }),
  );

  it('offers no create or delete to a user who is not a DataSourceAdmin', async () => {
    mswServer.use(oneSource);

    renderWithProviders(<DataSourcesListPage />);

    expect(await screen.findByText('ledger-db')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: /new data source/i })).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /delete data source ledger-db/i })).not.toBeInTheDocument();
  });

  it('offers a DataSourceAdmin create and delete', async () => {
    mswServer.use(oneSource, permissionsHandler({ permissions: ['DataSourceAdmin'] }));

    renderWithProviders(<DataSourcesListPage />);

    expect(await screen.findByRole('button', { name: /new data source/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /delete data source ledger-db/i })).toBeInTheDocument();
  });

  it('lets a DataSourceAdmin with no Viewer or Editor role add a data source, without listing them', async () => {
    // onUnhandledRequest is 'error': listing the data sources would fail this test.
    mswServer.use(functionalOnly('DataSourceAdmin'));

    renderWithProviders(<DataSourcesListPage />);

    expect(await screen.findByText('Listing data sources needs a Viewer or Editor role')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /new data source/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /refresh/i })).not.toBeInTheDocument();
  });
});

describe('DataSourcesListPage state badges', () => {
  it('badges disabled sources and connections whose transport is not verified, and nothing else', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          items: [
            source({ id: 1, name: 'ledger-db', transportVerified: true, disabled: false }),
            source({ id: 2, name: 'crm-db', transportVerified: false, disabled: false }),
            source({ id: 3, name: 'old-db', transportVerified: null, disabled: true }),
          ],
          totalCount: 3,
          pageCount: 1,
        }),
      ),
    );

    renderWithProviders(<DataSourcesListPage />);

    const ledger = (await screen.findByText('ledger-db')).closest('[role="row"]') as HTMLElement;
    expect(within(ledger).queryByText('Transport not verified')).not.toBeInTheDocument();
    expect(within(ledger).queryByText('Disabled')).not.toBeInTheDocument();
    const crm = screen.getByText('crm-db').closest('[role="row"]') as HTMLElement;
    expect(within(crm).getByText('Transport not verified')).toBeInTheDocument();
    expect(within(crm).queryByText('Disabled')).not.toBeInTheDocument();
    const old = screen.getByText('old-db').closest('[role="row"]') as HTMLElement;
    expect(within(old).getByText('Disabled')).toBeInTheDocument();
    expect(within(old).queryByText('Transport not verified')).not.toBeInTheDocument();
  });
});
