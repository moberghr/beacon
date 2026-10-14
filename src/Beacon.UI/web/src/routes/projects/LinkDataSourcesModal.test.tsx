import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { LinkDataSourcesModal } from './LinkDataSourcesModal';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

const sources = http.get('*/beacon/api/data-sources', () =>
  HttpResponse.json({
    items: [
      { id: 11, name: 'ledger-db', dataSourceType: 'Database', databaseEngineType: 'PostgreSQL' },
      { id: 12, name: 'payments-db', dataSourceType: 'Database', databaseEngineType: 'MSSQL' },
    ],
    totalCount: 2,
    pageCount: 1,
  }),
);

const problem = (status: number, detail: string) =>
  HttpResponse.json({ title: detail, status, detail }, { status, headers: { 'Content-Type': 'application/problem+json' } });

function renderModal(onClose = vi.fn()) {
  renderWithProviders(
    <LinkDataSourcesModal open projectId={5} projectName="Ledger" linkedDataSourceIds={[11]} onClose={onClose} />,
  );
  return onClose;
}

describe('LinkDataSourcesModal', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('links nothing until a data source is picked', async () => {
    let posts = 0;
    mswServer.use(
      sources,
      http.post('*/beacon/api/projects/5/data-sources', () => {
        posts += 1;
        return HttpResponse.json({ projectId: 5, linkedDataSourceIds: [] });
      }),
    );

    renderModal();

    const dialog = await screen.findByRole('dialog', { name: /link data sources to ledger/i });
    await within(dialog).findByRole('checkbox', { name: /payments-db/i });
    const submit = within(dialog).getByRole('button', { name: /^link data source$/i });
    expect(submit).toBeDisabled();
    fireEvent.click(submit);
    expect(posts).toBe(0);
  });

  it.each([
    [403, 'Only an Admin can link a data source that no open project holds.'],
    [404, 'Data source 12 not found.'],
  ])('keeps the modal open and says why when the server answers %i', async (status, detail) => {
    mswServer.use(
      sources,
      http.post('*/beacon/api/projects/5/data-sources', () => problem(status, detail)),
    );

    const onClose = renderModal();

    const dialog = await screen.findByRole('dialog', { name: /link data sources to ledger/i });
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: /payments-db/i }));
    fireEvent.click(within(dialog).getByRole('button', { name: /^link data source$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(detail));
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog', { name: /link data sources to ledger/i })).toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });
});
