import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AddSubscriptionDialog } from './AddSubscriptionDialog';

function stubQueryEndpoints(queryId: number) {
  mswServer.use(
    http.get('*/beacon/api/queries', () =>
      HttpResponse.json({
        items: [
          {
            queryId,
            name: `Query ${queryId}`,
            description: null,
            subscriptionsCount: 0,
          },
        ],
        totalCount: 1,
        page: 1,
        pageSize: 20,
      }),
    ),
    http.get(`*/beacon/api/queries/${queryId}`, () =>
      HttpResponse.json({
        queryId,
        name: `Query ${queryId}`,
        description: null,
      }),
    ),
  );
}

describe('AddSubscriptionDialog (multi-step)', () => {
  it('walks through steps and POSTs the new subscription', async () => {
    stubQueryEndpoints(12);
    mswServer.use(
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({
          entries: [
            {
              id: 7,
              name: 'Ops',
              description: null,
              destination: 'ops@example.com',
              notificationType: 2,
              headersJson: null,
              bodyTemplate: null,
              subscriptionCount: 0,
            },
          ],
        }),
      ),
    );

    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/subscriptions', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({ success: true, message: null });
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={12} />,
    );

    // Step 1 — Query is preselected via initialQueryId; advance.
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 2 — Recipients: pick Ops.
    await screen.findByText(/Ops/);
    fireEvent.click(screen.getByRole('checkbox', { name: /Ops/i }));
    fireEvent.click(screen.getByTestId('stepper-next'));

    // Step 3 — Review: submit.
    await screen.findByRole('button', { name: /create subscription/i });
    fireEvent.click(screen.getByRole('button', { name: /create subscription/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured).toMatchObject({
      queryId: 12,
      cronExpression: '0 9 * * *',
      recipientIds: [7],
    });
  });

  it('blocks advancing past Recipients step when none picked', async () => {
    stubQueryEndpoints(5);
    mswServer.use(
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({ entries: [] }),
      ),
    );

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={5} />,
    );

    fireEvent.click(screen.getByTestId('stepper-next'));

    // On Recipients step now — try to advance without selecting any.
    await screen.findByText(/No recipients yet/i);
    fireEvent.click(screen.getByTestId('stepper-next'));

    await waitFor(() => {
      expect(screen.getByText(/Pick at least one recipient/i)).toBeInTheDocument();
    });

    expect(onClose).not.toHaveBeenCalled();
  });
});
