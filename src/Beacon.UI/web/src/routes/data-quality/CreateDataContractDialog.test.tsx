import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { CreateDataContractDialog } from './CreateDataContractDialog';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

const DATA_SOURCES = {
  totalCount: 1,
  pageCount: 1,
  items: [
    {
      id: 9,
      name: 'finance-db',
      dataSourceType: 'Database',
      databaseEngineType: 'PostgreSQL',
      queryCount: 0,
      migrationJobsCount: 0,
      metadataLoadingEnabled: true,
    },
  ],
};

describe('CreateDataContractDialog', () => {
  it("keeps the server's reason in the dialog when a new contract is refused (400), without a toast", async () => {
    const reason =
      "Table sales.orders is not in the data source's metadata: a rule may only name the data source's own tables. Refresh the data source's metadata if the table is new.";
    mswServer.use(
      http.get('*/beacon/api/data-sources', () => HttpResponse.json(DATA_SOURCES)),
      http.get('*/beacon/api/data-sources/9', () => HttpResponse.json(DATA_SOURCES.items[0])),
      http.get('*/beacon/api/recipients', () => HttpResponse.json({ totalCount: 0, pageCount: 0, items: [] })),
      http.post('*/beacon/api/data-quality/contracts', () =>
        HttpResponse.json({ type: '/errors/invalid-operation', title: reason, status: 400 }, { status: 400 }),
      ),
    );
    const onClose = vi.fn();

    renderWithProviders(<CreateDataContractDialog editContractId={null} onClose={onClose} />);

    const dialog = await screen.findByRole('dialog');
    fireEvent.input(within(dialog).getByLabelText(/Contract name/), { target: { value: 'Orders are complete' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Data source' }));
    fireEvent.click(await screen.findByRole('option', { name: /finance-db/ }));
    fireEvent.input(within(dialog).getByLabelText(/Schema name/), { target: { value: 'sales' } });
    fireEvent.input(within(dialog).getByLabelText(/Table name/), { target: { value: 'orders' } });
    // Submits the form as Create does.
    fireEvent.submit(dialog.querySelector('form#data-contract-form')!);

    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent('The contract was not created');
    expect(alert).toHaveTextContent(reason);
    expect(onClose).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();
  });
});
