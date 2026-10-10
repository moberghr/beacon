import { screen, waitFor, within } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { ProjectRole } from '@/lib/enums';
import ProjectsListPage from './ProjectsListPage';
import { renderWithProviders } from '@/test/render';

describe('ProjectsListPage', () => {
  it('renders rows from /beacon/api/projects', async () => {
    renderWithProviders(<ProjectsListPage />);

    await waitFor(() => {
      expect(screen.getByText('Acme Analytics')).toBeInTheDocument();
      expect(screen.getByText('Beta Pipeline')).toBeInTheDocument();
    });

    // Header row + 2 data rows = 2 in the count line ("2 total")
    expect(screen.getByText(/2 total/)).toBeInTheDocument();
  });

  it('marks restricted projects and shows the caller\'s role', async () => {
    mswServer.use(
      http.get('*/beacon/api/projects', () =>
        HttpResponse.json({
          totalCount: 2,
          pageCount: 1,
          items: [
            { id: 1, name: 'Ledger', description: null, dataSourceCount: 1, repositoryCount: 0, lastScanAt: null, createdAt: '2026-10-01T10:00:00Z', requiresMembership: true, myRole: ProjectRole.Editor, memberCount: 2 },
            { id: 2, name: 'Marketing', description: null, dataSourceCount: 1, repositoryCount: 0, lastScanAt: null, createdAt: '2026-10-01T10:00:00Z', requiresMembership: false, myRole: null, memberCount: 0 },
          ],
        }),
      ),
    );

    renderWithProviders(<ProjectsListPage />);

    const ledger = (await screen.findByText('Ledger')).closest('[role="row"]') as HTMLElement;
    expect(within(ledger).getByText(/restricted/i)).toBeInTheDocument();
    expect(within(ledger).getByText('Editor')).toBeInTheDocument();
    const marketing = screen.getByText('Marketing').closest('[role="row"]') as HTMLElement;
    expect(within(marketing).queryByText(/restricted/i)).not.toBeInTheDocument();
  });
});
