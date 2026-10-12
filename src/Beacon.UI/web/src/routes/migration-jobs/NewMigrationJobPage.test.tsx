import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../../vitest.setup';
import NewMigrationJobPage from './NewMigrationJobPage';

interface Source {
  id: number;
  allowMigrationWrites: boolean;
}

/** The data-source list as the server filters it: databases only, write targets or read sources only. */
function serveSources(sources: Source[]) {
  const asked: { migrationTargets: string | null; databaseOnly: string | null }[] = [];
  mswServer.use(
    http.get('*/beacon/api/data-sources', ({ request }) => {
      const params = new URL(request.url).searchParams;
      const migrationTargets = params.get('migrationTargets');
      asked.push({ migrationTargets, databaseOnly: params.get('databaseOnly') });
      const items = sources
        .filter(x => migrationTargets === null || String(x.allowMigrationWrites) === migrationTargets)
        .map(x => ({
          id: x.id,
          name: `Source ${x.id}`,
          dataSourceType: 'Database',
          databaseEngineType: 'PostgreSQL',
          queryCount: 0,
          migrationJobsCount: 0,
          metadataLoadingEnabled: true,
          allowMigrationWrites: x.allowMigrationWrites,
        }));
      return HttpResponse.json({ totalCount: items.length, pageCount: 1, items });
    }),
  );
  return asked;
}

const NO_TARGET = /No data source is a migration write target yet/;
const TOO_FEW = /need at least two database data sources/;

describe('NewMigrationJobPage', () => {
  it('says no data source is a write target yet when none is', async () => {
    const asked = serveSources([
      { id: 1, allowMigrationWrites: false },
      { id: 2, allowMigrationWrites: false },
    ]);

    renderWithProviders(<NewMigrationJobPage />, { initialEntries: ['/migration-jobs/new'] });

    expect(await screen.findByText(NO_TARGET)).toBeInTheDocument();
    expect(screen.queryByText(TOO_FEW)).not.toBeInTheDocument();
    expect(asked).toContainEqual({ migrationTargets: 'true', databaseOnly: null });
  });

  it('shows no such notice once a data source is a write target', async () => {
    const asked = serveSources([
      { id: 1, allowMigrationWrites: false },
      { id: 2, allowMigrationWrites: true },
    ]);

    renderWithProviders(<NewMigrationJobPage />, { initialEntries: ['/migration-jobs/new'] });

    await waitFor(() => expect(asked).toContainEqual({ migrationTargets: 'true', databaseOnly: null }));
    await waitFor(() => expect(asked.some(x => x.databaseOnly === 'true')).toBe(true));
    expect(await screen.findByText('Basics')).toBeInTheDocument();
    expect(screen.queryByText(NO_TARGET)).not.toBeInTheDocument();
    expect(screen.queryByText(TOO_FEW)).not.toBeInTheDocument();
  });

  it('asks for two database sources first, before any write target', async () => {
    serveSources([{ id: 1, allowMigrationWrites: false }]);

    renderWithProviders(<NewMigrationJobPage />, { initialEntries: ['/migration-jobs/new'] });

    expect(await screen.findByText(TOO_FEW)).toBeInTheDocument();
    expect(screen.queryByText(NO_TARGET)).not.toBeInTheDocument();
  });
});
