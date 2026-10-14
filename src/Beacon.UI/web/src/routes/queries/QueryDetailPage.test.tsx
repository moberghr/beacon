import { Routes, Route } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup'; // path from src/routes/queries → repo web/
import QueryDetailPage from './QueryDetailPage';
import { renderWithProviders } from '@/test/render';

const SAMPLE_DETAIL = {
  id: 42,
  name: 'Daily revenue alert',
  description: 'Pings finance when daily revenue dips below threshold.',
  createdTime: '2026-04-15T10:00:00Z',
  totalExecutions: 12,
  sentNotifications: 7,
  steps: [
    {
      stepId: 1,
      stepOrder: 1,
      name: 'Fetch revenue',
      description: null,
      sqlValue: 'SELECT * FROM beacon.revenue',
      dataSourceId: 9,
      dataSourceName: 'finance-db',
      dataSourceType: 0,
      databaseEngineType: 1,
      databaseEngineDescription: 'PostgreSQL',
      parameters: [],
    },
  ],
  finalQuery: null,
  finalQueryDataSourceId: null,
  aiActorId: null,
  aiActorName: null,
  activeVersionId: 5,
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

describe('QueryDetailPage', () => {
  it('renders the hero, KPI counts, and lock button from real backend payload', async () => {
    mswServer.use(
      http.get('*/beacon/api/queries/42', () => HttpResponse.json(SAMPLE_DETAIL)),
      http.get('*/beacon/api/queries/42/versions', () => HttpResponse.json([])),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/queries/:id" element={<QueryDetailPage />} />
      </Routes>,
      { initialEntries: ['/queries/42'] },
    );

    // Hero name renders.
    await waitFor(() => {
      expect(screen.getByText('Daily revenue alert')).toBeInTheDocument();
    });

    // KPI grid: "Executions" label and the value 12 are both shown.
    // ("Executions" also appears as a tab title — getAllByText handles that.)
    expect(screen.getAllByText('Executions').length).toBeGreaterThan(0);
    expect(screen.getAllByText('12').length).toBeGreaterThan(0);

    // Save bar exposes the lock toggle.
    expect(screen.getByRole('button', { name: /Lock/i })).toBeInTheDocument();

    // Steps card shows the SQL.
    expect(screen.getByText(/SELECT \* FROM beacon.revenue/)).toBeInTheDocument();
  });

  // With the approval workflow on, a new query has no live steps until its first version is approved.
  it.each([
    [3, true],
    [4, false],
  ])('says a new query waits for its first approval (version status %s: %s)', async (status, shown) => {
    mswServer.use(
      http.get('*/beacon/api/queries/42', () => HttpResponse.json({ ...SAMPLE_DETAIL, steps: [], activeVersionId: null })),
      http.get('*/beacon/api/queries/42/versions', () =>
        HttpResponse.json({ items: [{ id: 30, versionNumber: 1, status, name: 'Daily revenue alert' }], totalCount: 1, pageCount: 1 }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/queries/:id" element={<QueryDetailPage />} />
      </Routes>,
      { initialEntries: ['/queries/42'] },
    );

    await screen.findByText('Daily revenue alert');
    const message = "This query's first version is waiting for approval; nothing runs until it is approved.";
    if (shown) {
      expect(await screen.findByText(message)).toBeInTheDocument();
      return;
    }

    await waitFor(() => expect(screen.queryByText(message)).not.toBeInTheDocument());
  });
});
