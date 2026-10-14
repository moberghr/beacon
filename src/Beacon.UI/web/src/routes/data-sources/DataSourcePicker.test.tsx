import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { DataSourcePicker } from './DataSourcePicker';

const sources = [
  { id: 1, name: 'Warehouse', dataSourceType: 'Database', databaseEngineType: 'PostgreSQL', allowMigrationWrites: false },
  { id: 2, name: 'Reporting', dataSourceType: 'Database', databaseEngineType: 'MSSQL', allowMigrationWrites: true },
];

function serveSources() {
  mswServer.use(
    http.get('*/beacon/api/data-sources', ({ request }) => {
      const targets = new URL(request.url).searchParams.get('migrationTargets');
      const items = sources.filter(x => targets === null || String(x.allowMigrationWrites) === targets);
      return HttpResponse.json({ totalCount: items.length, pageCount: 1, items });
    }),
  );
}

describe('DataSourcePicker', () => {
  it('offers only the sources Beacon reads, never a migration write target, unless asked for targets', async () => {
    serveSources();

    renderWithProviders(<DataSourcePicker value={null} onSelect={vi.fn()} ariaLabel="Data source" />);
    fireEvent.click(screen.getByRole('button', { name: 'Data source' }));

    expect(await screen.findByRole('option', { name: /Warehouse/ })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /Reporting/ })).not.toBeInTheDocument();
  });

  it('offers only migration write targets to a migration destination', async () => {
    serveSources();

    renderWithProviders(<DataSourcePicker value={null} onSelect={vi.fn()} migrationTargets ariaLabel="Destination" />);
    fireEvent.click(screen.getByRole('button', { name: 'Destination' }));

    expect(await screen.findByRole('option', { name: /Reporting/ })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /Warehouse/ })).not.toBeInTheDocument();
  });
});
