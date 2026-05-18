import { Routes, Route } from 'react-router-dom';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import NewQueryPage from './NewQueryPage';
import { renderWithProviders } from '@/test/render';

vi.mock('@monaco-editor/react', () => ({
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
});
