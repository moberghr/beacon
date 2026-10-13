import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { screen, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { NotificationStatus } from '@/lib/enums';
import { ExecutionsTab } from '@/routes/subscriptions/parts/ExecutionsTab';
import type { SubscriptionDetailRecipient } from '@/routes/subscriptions/queries';
import { parseRunComment, RunComment } from './RunComment';

const HELD = 'Skipped: where the recipient delivers waits for approval. Approve its change on the Approvals page, or put back its approved destination.';
const NOBODY = "Skipped: every recipient waits for approval of where it delivers, so nobody was notified. Approve the recipients' changes on the Approvals page.";
const TIMED_OUT = 'Notification delivery failed: the destination did not respond in time.';

describe("a run's recorded comment", () => {
  it('keeps a reason for the whole run as it is', () => {
    expect(parseRunComment(NOBODY)).toEqual([{ reason: NOBODY, recipientIds: [] }]);
    expect(parseRunComment(null)).toEqual([]);
    expect(parseRunComment('')).toEqual([]);
  });

  it('groups the per-recipient reasons by reason, keeping the recipients each names', () => {
    expect(parseRunComment(`Recipient 7: ${HELD} Recipient 9: ${TIMED_OUT} Recipient 12: ${HELD}`)).toEqual([
      { reason: HELD, recipientIds: [7, 12] },
      { reason: TIMED_OUT, recipientIds: [9] },
    ]);
  });

  it('names the recipients a reason is about, by name where it is known and by id otherwise', () => {
    renderWithProviders(
      <RunComment comment={`Recipient 7: ${HELD} Recipient 12: ${HELD}`} recipientNames={new Map([[7, 'Ops channel']])} />,
    );

    const notes = screen.getByRole('list', { name: 'Run notes' });
    expect(within(notes).getAllByRole('listitem')).toHaveLength(1);
    expect(within(notes).getByText('Ops channel, Recipient #12:')).toBeInTheDocument();
    expect(within(notes).getByText(new RegExp(HELD.slice(0, 40)))).toBeInTheDocument();
  });
});

describe("a subscription's execution history", () => {
  it('says why a run notified nobody, and names the recipients held back by approval', async () => {
    mswServer.use(
      http.get('*/beacon/api/notifications', () =>
        HttpResponse.json({
          items: [
            {
              id: 1002, subscriptionId: 42, queryName: 'orders-overdue', status: NotificationStatus.Skipped, resultCount: 3,
              executionTimeMs: 0, createdTime: '2026-10-13T09:00:00Z', aiActorId: null, aiActorName: null, comment: NOBODY, recipientNames: [],
            },
            {
              id: 1001, subscriptionId: 42, queryName: 'orders-overdue', status: NotificationStatus.NotificationSent, resultCount: 3,
              executionTimeMs: 0, createdTime: '2026-10-12T09:00:00Z', aiActorId: null, aiActorName: null,
              comment: `Recipient 7: ${HELD}`, recipientNames: ['Risk inbox'],
            },
          ],
          totalCount: 2,
          pageCount: 1,
        }),
      ),
    );
    const recipients = [
      { id: 7, name: 'Ops channel', description: null, destination: null, notificationType: 4, awaitingApproval: true },
      { id: 8, name: 'Risk inbox', description: null, destination: null, notificationType: 2, awaitingApproval: false },
    ] as SubscriptionDetailRecipient[];

    renderWithProviders(<ExecutionsTab subscriptionId={42} recipients={recipients} />);

    const skipped = (await screen.findByRole('link', { name: '#1002' })).closest('[role="row"]') as HTMLElement;
    expect(within(skipped).getByText(NOBODY)).toBeInTheDocument();
    const sent = screen.getByRole('link', { name: '#1001' }).closest('[role="row"]') as HTMLElement;
    expect(within(sent).getByText('Ops channel:')).toBeInTheDocument();
    expect(within(sent).getByText(new RegExp(HELD.slice(0, 40)))).toBeInTheDocument();
  });
});
