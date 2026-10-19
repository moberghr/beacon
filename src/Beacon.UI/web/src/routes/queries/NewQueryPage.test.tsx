import { Routes, Route } from 'react-router-dom';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import NewQueryPage from './NewQueryPage';
import { renderWithProviders } from '@/test/render';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

vi.mock('@/components/ui/monacoLocal', () => ({
  loadLocalMonaco: async () => ({}),
}));

vi.mock('@monaco-editor/react', () => ({
  loader: { config: vi.fn() },
  default: ({
    value,
    onChange,
  }: {
    value: string;
    onChange?: (next: string | undefined) => void;
  }) => (
    <textarea
      data-testid="monaco-stub"
      value={value}
      onChange={e => onChange?.(e.target.value)}
    />
  ),
}));

const DATA_SOURCES = {
  entries: [
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

describe('NewQueryPage', () => {
  it('renders the create form, disables Save until name is provided, and adds steps', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources', () => HttpResponse.json(DATA_SOURCES)),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/queries/new" element={<NewQueryPage />} />
      </Routes>,
      { initialEntries: ['/queries/new'] },
    );

    // Hero copy renders.
    expect(await screen.findByText(/Compose a/i)).toBeInTheDocument();

    // First step starts present with placeholder name "Step 1".
    expect(screen.getByDisplayValue('Step 1')).toBeInTheDocument();

    // Save buttons disabled while name is empty (there are two — header + save-bar).
    const saveButtons = screen.getAllByRole('button', { name: /Save query/i });
    expect(saveButtons.length).toBeGreaterThan(0);
    saveButtons.forEach(b => expect(b).toBeDisabled());

    // Pre-flight check warns about missing name.
    expect(screen.getByText(/Query name is required/i)).toBeInTheDocument();

    // Type a name → Save buttons enable.
    const nameInput = screen.getByLabelText(/Query name/i) as HTMLInputElement;
    fireEvent.change(nameInput, { target: { value: 'my new q' } });
    await waitFor(() => {
      screen.getAllByRole('button', { name: /Save query/i }).forEach(b => expect(b).not.toBeDisabled());
    });

    // Add step → Step 2 appears.
    fireEvent.click(screen.getByRole('button', { name: /Add step/i }));
    expect(await screen.findByDisplayValue('Step 2')).toBeInTheDocument();
  });

  it("keeps a refused save on the page with the server's reason (400), without a toast", async () => {
    vi.mocked(toast.error).mockClear();
    const reason = 'A query with this name already exists in the folder.';
    mswServer.use(
      http.get('*/beacon/api/data-sources', () => HttpResponse.json(DATA_SOURCES)),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
      ),
      http.post('*/beacon/api/queries', () =>
        HttpResponse.json({ type: '/errors/invalid-operation', title: reason, status: 400 }, { status: 400 }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/queries/new" element={<NewQueryPage />} />
        <Route path="/queries/:id" element={<div>query page</div>} />
      </Routes>,
      { initialEntries: ['/queries/new'] },
    );

    fireEvent.change(await screen.findByLabelText(/Query name/i), { target: { value: 'Late payments' } });
    const save = screen.getAllByRole('button', { name: /Save query/i })[0];
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The query was not saved');
    expect(alert).toHaveTextContent(reason);
    expect(screen.queryByText('query page')).not.toBeInTheDocument();
    expect(toast.error).not.toHaveBeenCalled();
  });
});
