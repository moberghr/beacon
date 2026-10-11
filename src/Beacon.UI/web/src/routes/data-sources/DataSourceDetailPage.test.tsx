import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import DataSourceDetailPage from './DataSourceDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}));

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

function renderWith(payload: Record<string, unknown>, ...extra: Parameters<typeof mswServer.use>) {
  mswServer.use(
    ...extra,
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

describe('DataSourceDetailPage metadata refresh', () => {
  beforeEach(() => {
    vi.mocked(toast.warning).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('says when the metadata was refreshed too recently (429), as a warning rather than a failure', async () => {
    const cooldown = "This data source's metadata was refreshed less than 15 minutes ago; try again later.";
    renderWith(
      entry({}),
      http.post('*/beacon/api/data-sources/42/refresh-metadata', () =>
        HttpResponse.json({ title: cooldown, status: 429 }, { status: 429 }),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith(cooldown));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("reports any other refusal as a failure with the server's reason", async () => {
    renderWith(
      entry({}),
      http.post('*/beacon/api/data-sources/42/refresh-metadata', () =>
        HttpResponse.json({ title: 'Metadata loading is disabled for this data source.', status: 400 }, { status: 400 }),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /refresh metadata/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Metadata loading is disabled for this data source.'));
  });
});
