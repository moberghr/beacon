import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import DataSourceDetailPage from './DataSourceDetailPage';

function entry(overrides: Record<string, unknown>) {
  return {
    id: 42,
    name: 'ledger-db',
    dataSourceType: 'Database',
    databaseEngineType: 'PostgreSQL',
    queryCount: 0,
    migrationJobsCount: 0,
    metadataLoadingEnabled: true,
    openWhenUnlinked: true,
    projects: [],
    otherProjectCount: 0,
    ...overrides,
  };
}

function renderWith(payload: Record<string, unknown>) {
  mswServer.use(
    http.get('*/beacon/api/data-sources/42', () => HttpResponse.json(payload)),
    http.get('*/beacon/api/data-sources/42/metadata', () => HttpResponse.json({ tables: [] })),
    http.get('*/beacon/api/*', () => HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })),
  );
  renderWithProviders(
    <Routes>
      <Route path="/data-sources/:id" element={<DataSourceDetailPage />} />
    </Routes>,
    { initialEntries: ['/data-sources/42'] },
  );
}

describe('DataSourceDetailPage projects', () => {
  it('links the readable projects to their pages and counts the others without naming them', async () => {
    renderWith(entry({ projects: [{ id: 5, name: 'Ledger' }, { id: 6, name: 'Audit' }], otherProjectCount: 2 }));

    expect(await screen.findByRole('link', { name: 'Ledger' })).toHaveAttribute('href', '/projects/5');
    expect(screen.getByRole('link', { name: 'Audit' })).toHaveAttribute('href', '/projects/6');
    expect(screen.getByText('+2 other')).toBeInTheDocument();
  });

  it('marks a source no project links, and that is not open, as Admin-only', async () => {
    renderWith(entry({ openWhenUnlinked: false }));

    expect(await screen.findByText('Admins only')).toBeInTheDocument();
    expect(screen.queryByText('Not linked')).not.toBeInTheDocument();
  });
});
