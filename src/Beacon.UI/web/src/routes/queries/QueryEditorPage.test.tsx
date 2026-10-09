import { Link, Routes, Route } from 'react-router-dom';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import QueryEditorPage from './QueryEditorPage';
import { renderWithProviders } from '@/test/render';

// Monaco depends on a real DOM environment + dynamic loader; replace the
// editor with a minimal textarea so the smoke tests stay deterministic.
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

const QUERY_DETAIL = {
  id: 99,
  name: 'Sample editor query',
  description: 'Editable test',
  createdTime: '2026-04-15T10:00:00Z',
  totalExecutions: 0,
  sentNotifications: 0,
  steps: [
    {
      stepId: 11,
      stepOrder: 1,
      name: 'Step 1',
      description: null,
      sqlValue: 'SELECT 1',
      dataSourceId: 9,
      dataSourceName: 'finance-db',
      dataSourceType: 1,
      databaseEngineType: 1,
      databaseEngineDescription: 'PostgreSQL',
      parameters: [],
    },
  ],
  finalQuery: null,
  finalQueryDataSourceId: null,
  aiActorId: null,
  aiActorName: null,
  isLocked: false,
  subscriptions: [],
  notificationHistory: [],
  avgExecutionTimeMs: 0,
  minExecutionTimeMs: 0,
  maxExecutionTimeMs: 0,
  executionTimeHistory: [],
  isMultiStep: false,
  isCrossDataSource: false,
  isCrossDatabase: false,
  dataSourceNames: ['finance-db'],
};

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

describe('QueryEditorPage', () => {
  it('renders the existing step with its SQL and supports adding a new step', async () => {
    mswServer.use(
      http.get('*/beacon/api/queries/99', () => HttpResponse.json(QUERY_DETAIL)),
      http.get('*/beacon/api/data-sources', () => HttpResponse.json(DATA_SOURCES)),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/queries/:id/edit" element={<QueryEditorPage />} />
      </Routes>,
      { initialEntries: ['/queries/99/edit'] },
    );

    // Initial render shows the query name and the existing SQL.
    await waitFor(() => {
      expect(screen.getByDisplayValue('Sample editor query')).toBeInTheDocument();
    });

    // SqlEditor is lazy — wait for the (mocked) Monaco to resolve.
    await waitFor(() => {
      expect(screen.getByDisplayValue('SELECT 1')).toBeInTheDocument();
    });
    expect(screen.getByDisplayValue('Step 1')).toBeInTheDocument();

    // Adding a step appends a new row with default name "Step 2".
    fireEvent.click(screen.getByRole('button', { name: /Add step/i }));
    expect(await screen.findByDisplayValue('Step 2')).toBeInTheDocument();
  });

  it('runs the unsaved SQL as a draft without saving the query', async () => {
    stubEditorEndpoints();
    let saved = false;
    let previewBody: { draft?: { steps: { sqlValue: string }[] } | null } | null = null;
    mswServer.use(
      http.put('*/beacon/api/queries/99', () => {
        saved = true;
        return HttpResponse.json({ queryId: 99, success: true, message: 'Saved successfully' });
      }),
      http.post('*/beacon/api/queries/99/preview', async ({ request }) => {
        previewBody = (await request.json()) as typeof previewBody;
        return HttpResponse.json({ success: true, steps: [], dataSourcesInvolved: [], totalExecutionTimeMs: 1 });
      }),
    );

    renderEditor();
    fireEvent.change(await screen.findByDisplayValue('SELECT 1'), { target: { value: 'SELECT 2' } });
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Run query/i }));

    await waitFor(() => {
      expect(previewBody?.draft?.steps[0]?.sqlValue).toBe('SELECT 2');
    });
    expect(saved).toBe(false);
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
  });

  it('asks before leaving with unsaved changes', async () => {
    stubEditorEndpoints();

    renderEditor();
    fireEvent.change(await screen.findByDisplayValue('Sample editor query'), { target: { value: 'Renamed' } });

    // Cancel asks first; keeping the edits stays on the editor.
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/ }));
    expect(await screen.findByText('Discard unsaved changes?')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Keep editing/i }));
    await waitFor(() => {
      expect(screen.queryByText('Discard unsaved changes?')).toBeNull();
    });
    expect(screen.getByDisplayValue('Renamed')).toBeInTheDocument();

    // An ordinary in-app link (the sidebar) is held too.
    fireEvent.click(screen.getByRole('link', { name: 'Projects' }));
    expect(await screen.findByText('Discard unsaved changes?')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Keep editing/i }));
    expect(screen.queryByText('Projects page')).toBeNull();

    // Version history leaves once confirmed.
    fireEvent.click(screen.getByRole('button', { name: /Version history/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Discard and leave/i }));
    expect(await screen.findByText('Versions page')).toBeInTheDocument();
  });

  it('leaves without asking when nothing changed', async () => {
    stubEditorEndpoints();

    renderEditor();
    await screen.findByDisplayValue('Sample editor query');

    fireEvent.click(screen.getByRole('link', { name: 'Projects' }));

    expect(await screen.findByText('Projects page')).toBeInTheDocument();
    expect(screen.queryByText('Discard unsaved changes?')).toBeNull();
  });
});

function stubEditorEndpoints() {
  mswServer.use(
    http.get('*/beacon/api/queries/99', () => HttpResponse.json(QUERY_DETAIL)),
    http.get('*/beacon/api/data-sources', () => HttpResponse.json(DATA_SOURCES)),
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
    ),
  );
}

/** The editor with a sidebar-like link next to it and the pages it can leave to. */
function renderEditor() {
  return renderWithProviders(
    <Routes>
      <Route
        path="/queries/:id/edit"
        element={
          <>
            <Link to="/projects">Projects</Link>
            <QueryEditorPage />
          </>
        }
      />
      <Route path="/queries/:id/versions" element={<div>Versions page</div>} />
      <Route path="/queries/:id" element={<div>Query page</div>} />
      <Route path="/projects" element={<div>Projects page</div>} />
    </Routes>,
    { initialEntries: ['/queries/99/edit'] },
  );
}
