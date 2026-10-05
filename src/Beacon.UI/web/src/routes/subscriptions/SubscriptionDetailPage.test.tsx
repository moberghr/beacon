import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import SubscriptionDetailPage from './SubscriptionDetailPage';

function detailPayload(overrides: Record<string, unknown> = {}) {
  return {
    detail: {
      id: 42,
      queryId: 9,
      queryName: 'orders-overdue',
      status: 'Active',
      cronExpression: '0 9 * * *',
      cronDescription: 'At 09:00 every day',
      cronNextAt: '2026-05-09T09:00:00Z',
      aiActorId: null,
      aiActorName: null,
      maxRows: 100,
      minimumRowCount: null,
      includeAttachment: true,
      resultAttachmentType: 1,
      showQuery: true,
      timeoutSeconds: 60,
      storeResults: false,
      createTasks: false,
      notificationTrigger: 1,
      parameters: [],
      recipients: [
        {
          id: 7,
          name: 'Ops',
          description: null,
          destination: 'ops@example.com',
          notificationType: 2,
        },
      ],
      anomalyConfig: null,
      ...overrides,
    },
  };
}

describe('SubscriptionDetailPage', () => {
  it('renders hero, kpis and the recipients tab from real fetch data', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({
          userId: 'u1',
          displayName: 'Tester',
          email: 't@example.com',
          isAuthenticated: true,
          roles: ['Admin'],
        }),
      ),
      http.get('*/beacon/api/subscriptions/42', () =>
        HttpResponse.json(detailPayload()),
      ),
      http.get('*/beacon/api/notifications', () =>
        HttpResponse.json({
          items: [
            {
              id: 1001,
              subscriptionId: 42,
              queryName: 'orders-overdue',
              status: 2,
              resultCount: 5,
              executionTimeMs: 120,
              createdTime: '2026-05-07T10:00:00Z',
              aiActorId: null,
              aiActorName: null,
              comment: null,
              recipientNames: ['Ops'],
            },
          ],
          totalCount: 1,
          pageCount: 1,
        }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/subscriptions/:id" element={<SubscriptionDetailPage />} />
      </Routes>,
      { initialEntries: ['/subscriptions/42'] },
    );

    // Hero — query name appears as the emphasis word, and again in the
    // Query info card / right rail.
    await waitFor(() => {
      expect(screen.getAllByText('orders-overdue').length).toBeGreaterThan(0);
    });

    // KPI: total executions reflects totalCount from the executions endpoint.
    await waitFor(() => {
      expect(screen.getByText('Total executions')).toBeInTheDocument();
    });
    expect(screen.getAllByText('Recipients').length).toBeGreaterThan(0);

    // Recipients tab is the default — Ops recipient card should render.
    expect(screen.getAllByText('Ops').length).toBeGreaterThan(0);
    expect(screen.getByText('ops@example.com')).toBeInTheDocument();

    // Hero ACTIVE pill present.
    expect(screen.getAllByText('ACTIVE').length).toBeGreaterThan(0);
  });

  it('offers Reactivate instead of Archive for an archived subscription and posts to the reactivate endpoint', async () => {
    let status = 'Archived';
    let reactivateCalls = 0;
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({
          userId: 'u1',
          displayName: 'Tester',
          email: 't@example.com',
          isAuthenticated: true,
          roles: ['Admin'],
        }),
      ),
      http.get('*/beacon/api/subscriptions/42', () =>
        HttpResponse.json(detailPayload({ status })),
      ),
      http.post('*/beacon/api/subscriptions/42/reactivate', () => {
        reactivateCalls += 1;
        status = 'Active';
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('*/beacon/api/notifications', () =>
        HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/subscriptions/:id" element={<SubscriptionDetailPage />} />
      </Routes>,
      { initialEntries: ['/subscriptions/42'] },
    );

    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: /reactivate/i }).length).toBeGreaterThan(0);
    });
    expect(screen.queryByRole('button', { name: /^archive$/i })).not.toBeInTheDocument();

    const reactivate = screen.getAllByRole('button', { name: /reactivate/i })[0];
    await waitFor(() => expect(reactivate).toBeEnabled());
    fireEvent.click(reactivate);

    await waitFor(() => expect(reactivateCalls).toBe(1));
    // The detail refetches after the mutation, so the page flips back to the active lifecycle.
    await waitFor(() => {
      expect(screen.getAllByRole('button', { name: /^archive$/i }).length).toBeGreaterThan(0);
    });
  });
});
