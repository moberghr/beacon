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

  it('explains a 403 on test-connection instead of showing a bare status code', async () => {
    mswServer.use(
      http.post('*/beacon/api/data-sources/test-connection', () =>
        new HttpResponse(null, { status: 403 }),
      ),
    );

    renderWithProviders(<AddDataSourceDialog open onClose={vi.fn()} />);
    await advanceToTestStep();

    fireEvent.click(screen.getByRole('button', { name: /test connection/i }));

    expect(await screen.findByText(/restricted to admin accounts/i)).toBeInTheDocument();
    // The raw transport detail stays available even when the 403 carried no body.
    expect(screen.getByText(/show details/i)).toBeInTheDocument();
    expect(screen.getByText(/HTTP 403/)).toBeInTheDocument();
    expect(screen.getByText(/empty response body/i)).toBeInTheDocument();
  });

  it('keeps the driver exception chain behind a details toggle', async () => {
    mswServer.use(
      http.post('*/beacon/api/data-sources/test-connection', () =>
        HttpResponse.json({
          success: false,
          message:
            'Connection failed: SqlException: A network-related error occurred. -> Win32Exception: No such host is known.',
        }),
      ),
    );

    renderWithProviders(<AddDataSourceDialog open onClose={vi.fn()} />);
    await advanceToTestStep();

    fireEvent.click(screen.getByRole('button', { name: /test connection/i }));

    // Headline is the outermost frame only.
    expect(
      await screen.findByText('Connection failed: SqlException: A network-related error occurred.'),
    ).toBeInTheDocument();
    // The cause the operator actually needs is present, under "Show details".
    expect(screen.getByText(/No such host is known/)).toBeInTheDocument();
    expect(screen.getByText(/show details/i)).toBeInTheDocument();
  });
});

async function advanceToTestStep() {
  fireEvent.click(screen.getByTestId('stepper-next'));
  fireEvent.input(screen.getByLabelText(/^Name/), { target: { value: 'test server' } });
  fireEvent.input(screen.getByLabelText(/Connection string/i), {
    target: { value: 'Server=db;Database=app;User Id=u;Password=p' },
  });
  fireEvent.click(screen.getByTestId('stepper-next'));
  await screen.findByRole('button', { name: /test connection/i });
}
