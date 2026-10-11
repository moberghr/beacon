import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { NotificationStatus } from '@/lib/enums';
import SubscriptionDetailPage from './SubscriptionDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function signedIn(roles: string[]) {
  return http.get('*/beacon/api/auth/me', () =>
    HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: 't@example.com', isAuthenticated: true, roles }),
  );
}

function renderDetail() {
  return renderWithProviders(
    <Routes>
      <Route path="/subscriptions/:id" element={<SubscriptionDetailPage />} />
    </Routes>,
    { initialEntries: ['/subscriptions/42'] },
  );
}

const noExecutions = http.get('*/beacon/api/notifications', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }));

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
          description: 'Operations inbox',
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
    expect(screen.getByText('Operations inbox')).toBeInTheDocument();
    // Destinations are never shown on a subscription, even if a server sent one.
    expect(screen.queryByText('ops@example.com')).not.toBeInTheDocument();

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

  it('shows the owner the scheduled runs act for by name, never by id, or that none was recorded', async () => {
    let owner: Record<string, unknown> = { hasOwner: true, ownerDisplayName: 'Ada Lovelace' };
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: 't@example.com', isAuthenticated: true, roles: [] }),
      ),
      http.get('*/beacon/api/subscriptions/42', () => HttpResponse.json(detailPayload(owner))),
      http.get('*/beacon/api/notifications', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    );
    const renderPage = () =>
      renderWithProviders(
        <Routes>
          <Route path="/subscriptions/:id" element={<SubscriptionDetailPage />} />
        </Routes>,
        { initialEntries: ['/subscriptions/42'] },
      );

    const named = renderPage();
    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
    named.unmount();

    // An owner whose user no longer exists: a neutral label, even if a stray id field came along.
    owner = { hasOwner: true, ownerDisplayName: null, ownerUserId: 'ext-ada' };
    const unknown = renderPage();
    expect(await screen.findByText('unknown user')).toBeInTheDocument();
    expect(screen.queryByText('ext-ada')).not.toBeInTheDocument();
    unknown.unmount();

    owner = { hasOwner: false, ownerDisplayName: null };
    renderPage();
    expect(await screen.findByText('no owner recorded')).toBeInTheDocument();
  });

  it('shows the not-found state when the subscription answers 404', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: 't@example.com', isAuthenticated: true, roles: [] }),
      ),
      http.get('*/beacon/api/subscriptions/42', () => HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })),
      http.get('*/beacon/api/notifications', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    );

    renderWithProviders(
      <Routes>
        <Route path="/subscriptions/:id" element={<SubscriptionDetailPage />} />
      </Routes>,
      { initialEntries: ['/subscriptions/42'] },
    );

    expect(await screen.findByText('Subscription not found')).toBeInTheDocument();
    expect(screen.queryByText(/failed to load subscription/i)).not.toBeInTheDocument();
  });

  describe('owner not authorized', () => {
    beforeEach(() => {
      vi.mocked(toast.success).mockClear();
      vi.mocked(toast.error).mockClear();
    });

    it('tells a non-Admin that scheduled runs are skipped until an Admin assigns an owner, without offering the change', async () => {
      mswServer.use(
        signedIn([]),
        http.get('*/beacon/api/subscriptions/42', () =>
          HttpResponse.json(detailPayload({ hasOwner: true, ownerDisplayName: 'Ada Lovelace', ownerNotAuthorized: true })),
        ),
        noExecutions,
      );

      renderDetail();

      expect(await screen.findByText(/owner not authorized: scheduled runs are skipped/i)).toBeInTheDocument();
      expect(screen.getByText(/until an admin assigns an owner who has access/i)).toBeInTheDocument();
      expect(screen.getByText('not authorized')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /change owner/i })).not.toBeInTheDocument();
    });

    it('shows no banner while the owner may run the query', async () => {
      mswServer.use(
        signedIn(['Admin']),
        http.get('*/beacon/api/subscriptions/42', () =>
          HttpResponse.json(detailPayload({ hasOwner: true, ownerDisplayName: 'Ada Lovelace', ownerNotAuthorized: false })),
        ),
        noExecutions,
      );

      renderDetail();

      expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
      expect(screen.queryByText(/owner not authorized/i)).not.toBeInTheDocument();
      // An Admin can still change the owner from the header.
      expect(screen.getByRole('button', { name: /change owner/i })).toBeInTheDocument();
    });

    it("lets an Admin change the owner, keeps the dialog open with the server's reason on a 409, and saves on retry", async () => {
      let ownerNotAuthorized = true;
      const sent: unknown[] = [];
      mswServer.use(
        signedIn(['Admin']),
        http.get('*/beacon/api/subscriptions/42', () =>
          HttpResponse.json(detailPayload({ hasOwner: true, ownerDisplayName: 'Ada Lovelace', ownerNotAuthorized })),
        ),
        noExecutions,
        http.get('*/beacon/api/users', () =>
          HttpResponse.json({
            items: [
              { id: 3, userName: 'bea', displayName: 'Bea Analyst', email: 'bea@example.com' },
              { id: 4, userName: 'cy', displayName: 'Cy Viewer', email: 'cy@example.com' },
            ],
            totalCount: 2,
            pageCount: 1,
          }),
        ),
        http.put('*/beacon/api/subscriptions/42/owner', async ({ request }) => {
          const body = (await request.json()) as { userId: number };
          sent.push(body);
          if (body.userId === 4) {
            return HttpResponse.json(
              { title: "The new owner cannot run this subscription's query.", status: 409 },
              { status: 409 },
            );
          }
          ownerNotAuthorized = false;
          return new HttpResponse(null, { status: 204 });
        }),
      );

      renderDetail();

      // The banner's title sits two levels below the banner, next to its actions.
      const banner = (await screen.findByText(/owner not authorized: scheduled runs are skipped/i)).parentElement!.parentElement!;
      fireEvent.click(within(banner).getByRole('button', { name: /change owner/i }));

      const dialog = await screen.findByRole('dialog', { name: /change the subscription's owner/i });
      expect(within(dialog).getByText(/scheduled runs run as the owner/i)).toBeInTheDocument();

      fireEvent.click(within(dialog).getByRole('button', { name: 'New owner' }));
      fireEvent.click(await within(dialog).findByRole('option', { name: /Cy Viewer/ }));
      fireEvent.click(within(dialog).getByRole('button', { name: /save owner/i }));

      await waitFor(() =>
        expect(toast.error).toHaveBeenCalledWith("The new owner cannot run this subscription's query."),
      );
      expect(screen.getByRole('dialog', { name: /change the subscription's owner/i })).toBeInTheDocument();

      fireEvent.click(within(dialog).getByRole('button', { name: 'New owner' }));
      fireEvent.click(await within(dialog).findByRole('option', { name: /Bea Analyst/ }));
      fireEvent.click(within(dialog).getByRole('button', { name: /save owner/i }));

      await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Owner updated'));
      expect(sent).toEqual([{ userId: 4 }, { userId: 3 }]);
      await waitFor(() =>
        expect(screen.queryByRole('dialog', { name: /change the subscription's owner/i })).not.toBeInTheDocument(),
      );
      // The detail reloads: runs are no longer skipped.
      await waitFor(() => expect(screen.queryByText(/owner not authorized/i)).not.toBeInTheDocument());
    });

    it('labels a skipped run in the execution history and the latest run', async () => {
      mswServer.use(
        signedIn([]),
        http.get('*/beacon/api/subscriptions/42', () => HttpResponse.json(detailPayload({ hasOwner: true, ownerNotAuthorized: true }))),
        http.get('*/beacon/api/notifications', () =>
          HttpResponse.json({
            items: [
              {
                id: 1002,
                subscriptionId: 42,
                queryName: 'orders-overdue',
                status: NotificationStatus.Skipped,
                resultCount: 0,
                executionTimeMs: 0,
                createdTime: '2026-05-08T09:00:00Z',
                aiActorId: null,
                aiActorName: null,
                comment: 'Skipped: owner not authorized.',
                recipientNames: [],
              },
            ],
            totalCount: 1,
            pageCount: 1,
          }),
        ),
      );

      renderDetail();

      // The right rail's latest run.
      expect(await screen.findByText('Skipped')).toBeInTheDocument();
      fireEvent.click(screen.getByRole('tab', { name: /execution history/i }));
      await waitFor(() => expect(screen.getAllByText('Skipped')).toHaveLength(2));
    });
  });
});
