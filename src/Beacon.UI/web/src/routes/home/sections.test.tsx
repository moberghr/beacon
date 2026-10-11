import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import { mswServer } from '../../../vitest.setup';
import { MigrationOverviewCard } from './sections';

function renderCard() {
  mswServer.use(
    http.get('*/beacon/api/home/migration-summary', () =>
      HttpResponse.json({ total: 4, successful: 3, executions: 9, errored: 1 }),
    ),
  );
  return renderWithProviders(
    <>
      <MigrationOverviewCard />
      <AccessReady />
    </>,
  );
}

describe('MigrationOverviewCard', () => {
  it('links a MigrationOperator to the jobs', async () => {
    mswServer.use(permissionsHandler({ permissions: ['MigrationOperator'] }));

    renderCard();

    expect(await screen.findByRole('link', { name: 'Open jobs →' })).toHaveAttribute('href', '/migration-history');
  });

  it('offers no link to a holder of every other permission', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'UserAdmin', 'DataSourceAdmin'] }),
    );

    renderCard();

    expect(await screen.findByText('Data migration')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('link', { name: 'Open jobs →' })).not.toBeInTheDocument();
  });
});
