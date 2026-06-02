import { Routes, Route } from 'react-router-dom';
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
});
