import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { act, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import RecipientsListPage from './RecipientsListPage';

let meServed = false;

function me(roles: string[]) {
  meServed = false;
  return http.get('*/beacon/api/auth/me', () => {
    meServed = true;
    return HttpResponse.json({
      userId: 'u1',
      displayName: 'User',
      email: null,
      isAuthenticated: true,
      roles,
      realtimeEnabled: false,
      externalLogin: null,
    });
  });
}

const recipients = http.get('*/beacon/api/recipients', () =>
  HttpResponse.json({
    totalCount: 1,
    pageCount: 1,
    items: [
      {
        id: 1,
        name: 'Ops channel',
        description: null,
        destination: null,
        notificationType: 4, // Slack
        headersJson: null,
        bodyTemplate: null,
        subscriptionCount: 2,
      },
    ],
  }),
);

function recipientsOf(items: unknown[]) {
  return http.get('*/beacon/api/recipients', () =>
    HttpResponse.json({ totalCount: items.length, pageCount: items.length > 0 ? 1 : 0, items }),
  );
}

describe('RecipientsListPage', () => {
  it('shows a non-admin the recipients without create or delete controls', async () => {
    mswServer.use(me(['Editor']), recipients);

    renderWithProviders(<RecipientsListPage />);

    expect(await screen.findByText('Ops channel')).toBeInTheDocument();
    // Let the role lookup resolve first, so the absence below is not just "still loading".
    await waitFor(() => expect(meServed).toBe(true));
    await act(async () => {
      await new Promise(resolve => setTimeout(resolve, 0));
    });

    expect(screen.queryByRole('button', { name: /add recipient/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete ops channel/i })).not.toBeInTheDocument();
  });

  it('flags, for an admin, a recipient whose stored secrets cannot be read', async () => {
    mswServer.use(
      me(['Admin']),
      recipientsOf([
        {
          id: 1,
          name: 'Ops channel',
          description: null,
          destination: '********',
          notificationType: 4,
          headersJson: null,
          bodyTemplate: null,
          secretsUnreadable: true,
          subscriptionCount: 2,
        },
      ]),
    );

    renderWithProviders(<RecipientsListPage />);

    expect(await screen.findByText('Re-enter destination')).toBeInTheDocument();
  });

  it('tells a non-admin that an admin adds recipients when there are none', async () => {
    mswServer.use(me(['Editor']), recipientsOf([]));

    renderWithProviders(<RecipientsListPage />);

    await waitFor(() => expect(meServed).toBe(true));
    expect(await screen.findByText(/An admin adds recipients/)).toBeInTheDocument();
  });

  it('shows an admin the create and delete controls', async () => {
    mswServer.use(me(['Admin']), recipients);

    renderWithProviders(<RecipientsListPage />);

    expect(await screen.findByRole('button', { name: /add recipient/i })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /delete ops channel/i })).toBeInTheDocument();
  });
});
