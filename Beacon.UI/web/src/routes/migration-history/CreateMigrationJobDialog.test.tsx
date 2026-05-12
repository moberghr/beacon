import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { CreateMigrationJobDialog } from './CreateMigrationJobDialog';

describe('CreateMigrationJobDialog (multi-step)', () => {
  it('walks through the steps and POSTs the new migration job', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          entries: [
            {
              id: 1,
              name: 'Warehouse',
              dataSourceType: 'Database',
              databaseEngineType: 'PostgreSQL',
              queryCount: 0,
              migrationJobsCount: 0,
              metadataLoadingEnabled: true,
            },
            {
              id: 2,
              name: 'Reporting',
              dataSourceType: 'Database',
              databaseEngineType: 'MSSQL',
              queryCount: 0,
              migrationJobsCount: 0,
              metadataLoadingEnabled: true,
            },
          ],
        }),
      ),
    );

    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/migrations/jobs', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({
          migrationJobId: 42,
          success: true,
          errorMessage: null,
        });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<CreateMigrationJobDialog open onClose={onClose} />);

    // Step 1 — Basics.
    fireEvent.input(screen.getByLabelText(/^Job name/), { target: { value: 'Nightly users sync' } });
    fireEvent.input(screen.getByLabelText(/^Description/), {
      target: { value: 'Copy users to reporting' },
    });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 2 — Source: pick source DS, write SQL.
    const srcSelect = await screen.findByLabelText(/Source data source/) as HTMLSelectElement;
    // Wait for data-sources query to populate the option list.
    await waitFor(() => expect(srcSelect.querySelectorAll('option').length).toBeGreaterThan(1));
    fireEvent.change(srcSelect, { target: { value: '1' } });
    fireEvent.input(screen.getByLabelText(/Source SQL/), {
      target: { value: 'SELECT id, email FROM users' },
    });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 3 — Destination.
    await screen.findByLabelText(/Destination data source/);
    fireEvent.change(screen.getByLabelText(/Destination data source/), { target: { value: '2' } });
    fireEvent.input(screen.getByLabelText(/Destination table/), {
      target: { value: 'reporting.users' },
    });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 4 — Schedule.
    await screen.findByLabelText(/Cron schedule/);
    fireEvent.input(screen.getByLabelText(/Cron schedule/), { target: { value: '0 2 * * *' } });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 5 — Review & submit.
    await screen.findByRole('button', { name: /create migration job/i });
    fireEvent.click(screen.getByRole('button', { name: /create migration job/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured).toMatchObject({
      name: 'Nightly users sync',
      description: 'Copy users to reporting',
      dataSourceId: 1,
      destinationDataSourceId: 2,
      destinationTable: 'reporting.users',
      queryText: 'SELECT id, email FROM users',
      mode: 1,
      schedule: '0 2 * * *',
      isEnabled: true,
    });
  });

  it('blocks advancing past Source step when SQL is empty', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () =>
        HttpResponse.json({
          entries: [
            {
              id: 1,
              name: 'Warehouse',
              dataSourceType: 'Database',
              databaseEngineType: 'PostgreSQL',
              queryCount: 0,
              migrationJobsCount: 0,
              metadataLoadingEnabled: true,
            },
          ],
        }),
      ),
    );

    const onClose = vi.fn();
    renderWithProviders(<CreateMigrationJobDialog open onClose={onClose} />);

    fireEvent.input(screen.getByLabelText(/^Job name/), { target: { value: 'X' } });
    fireEvent.input(screen.getByLabelText(/^Description/), { target: { value: 'desc' } });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // On Source step — pick source but leave SQL empty, try to advance.
    const srcSelect = await screen.findByLabelText(/Source data source/) as HTMLSelectElement;
    await waitFor(() => expect(srcSelect.querySelectorAll('option').length).toBeGreaterThan(1));
    fireEvent.change(srcSelect, { target: { value: '1' } });
    fireEvent.click(screen.getByTestId('stepper-next'));

    await waitFor(() => {
      expect(screen.getByText(/Source SQL is required/i)).toBeInTheDocument();
    });

    expect(onClose).not.toHaveBeenCalled();
  });
});
