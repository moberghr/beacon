import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { screen, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { NotificationStatus } from '@/lib/enums';
import NotificationsPage from './NotificationsPage';

describe('NotificationsPage', () => {
  it('labels a skipped run, and offers Skipped as a status filter', async () => {
    mswServer.use(
      http.get('*/beacon/api/notifications', () =>
        HttpResponse.json({
          items: [
            {
              id: 5,
              subscriptionId: 2,
              queryName: 'revenue-join',
              status: NotificationStatus.Skipped,
              resultCount: 0,
              executionTimeMs: 0,
              createdTime: '2026-10-10T07:00:00Z',
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

    renderWithProviders(<NotificationsPage />, { initialEntries: ['/notifications'] });

    expect(await screen.findByText('Skipped: owner not authorized.')).toBeInTheDocument();
    const filter = screen.getByRole('combobox', { name: /filter by status/i });
    expect(within(filter).getByRole('option', { name: 'Skipped' })).toHaveValue(String(NotificationStatus.Skipped));
    // The status pill, besides the filter option.
    expect(screen.getAllByText('Skipped')).toHaveLength(2);
  });
});
