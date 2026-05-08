import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AddSubscriptionDialog } from './AddSubscriptionDialog';

describe('AddSubscriptionDialog', () => {
  it('POSTs the new subscription with selected recipients and closes on success', async () => {
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
    renderWithProviders(<AddSubscriptionDialog open onClose={onClose} />);

    // Wait for the recipients query to resolve and render the row.
    await screen.findByText(/Ops/);

    fireEvent.input(screen.getByLabelText(/^Query id/), { target: { value: '12' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Ops/i }));

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

  it('blocks submission with a validation error when no recipients are picked', async () => {
    mswServer.use(
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({ entries: [] }),
      ),
    );

    const onClose = vi.fn();
    renderWithProviders(<AddSubscriptionDialog open onClose={onClose} />);

    fireEvent.input(screen.getByLabelText(/^Query id/), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: /create subscription/i }));

    await waitFor(() => {
      expect(screen.getByText(/Pick at least one recipient/i)).toBeInTheDocument();
    });

    expect(onClose).not.toHaveBeenCalled();
  });
});
