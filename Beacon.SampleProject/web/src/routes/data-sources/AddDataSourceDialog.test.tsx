import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AddDataSourceDialog } from './AddDataSourceDialog';

describe('AddDataSourceDialog (multi-engine)', () => {
  it('switches engine sections to reveal kind-specific fields', async () => {
    const onClose = vi.fn();
    renderWithProviders(<AddDataSourceDialog open onClose={onClose} />);

    // Step 1 — Type. Default is Database; switch to Databricks and advance.
    const kindSelect = screen.getByLabelText(/data source type/i) as HTMLSelectElement;
    expect(kindSelect.value).toBe('Database');

    fireEvent.change(kindSelect, { target: { value: 'Databricks' } });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Connection step now exposes Databricks-only fields.
    await screen.findByLabelText(/^Host/i);
    expect(screen.getByLabelText(/HTTP path/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Personal access token/i)).toBeInTheDocument();

    // No Database-only fields are present.
    expect(screen.queryByLabelText(/Connection string/i)).toBeNull();
    expect(screen.queryByLabelText(/Database engine/i)).toBeNull();
  });

  it('walks Database flow and POSTs to /data-sources', async () => {
    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/data-sources', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({ success: true, message: 'Created.' });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<AddDataSourceDialog open onClose={onClose} />);

    // Step 1 — Type stays on Database.
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 2 — Connection.
    fireEvent.input(screen.getByLabelText(/^Name/), { target: { value: 'analytics' } });
    fireEvent.input(screen.getByLabelText(/Connection string/i), {
      target: { value: 'Host=db;Database=app;Username=u;Password=p' },
    });
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 3 — Test & save. Submit.
    await screen.findByRole('button', { name: /create data source/i });
    fireEvent.click(screen.getByRole('button', { name: /create data source/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured).toMatchObject({
      name: 'analytics',
      dataSourceType: 1,
      databaseEngineType: 1,
      connectionString: 'Host=db;Database=app;Username=u;Password=p',
    });
  });
});
