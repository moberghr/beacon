import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import MigrationJobsListPage from './MigrationJobsListPage';

const noJobs = http.get('*/beacon/api/migrations/jobs', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }));

describe('MigrationJobsListPage', () => {
  it('tells a MigrationOperator with no Viewer or Editor role why no job is listed', async () => {
    mswServer.use(functionalOnly('MigrationOperator'), noJobs);

    renderWithProviders(<MigrationJobsListPage />);

    expect(await screen.findByText('No migration jobs you can see')).toBeInTheDocument();
    expect(screen.getByText(/needs read access to its data sources and write access to its destination/)).toBeInTheDocument();
  });

  it('invites a MigrationOperator who reads project data to create the first job', async () => {
    mswServer.use(permissionsHandler({ permissions: ['MigrationOperator'] }), noJobs);

    renderWithProviders(<MigrationJobsListPage />);

    expect(await screen.findByText('No migration jobs yet')).toBeInTheDocument();
    expect(screen.queryByText('No migration jobs you can see')).not.toBeInTheDocument();
  });
});
