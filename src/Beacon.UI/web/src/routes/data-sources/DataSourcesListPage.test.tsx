import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { screen, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
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
