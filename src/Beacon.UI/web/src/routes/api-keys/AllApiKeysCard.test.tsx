import { describe, it, expect, beforeEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AllApiKeysCard } from './AllApiKeysCard';

const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' }));

const key = (id: number, name: string, userId: number, userName: string, isActive: boolean) => ({
  id,
  name,
  prefix: 'sk-sem_abcdefgh',
  scopes: ['Read'],
  allowedProjectIds: null,
  createdAt: '2026-06-02T09:00:00Z',
  lastUsedAt: null,
  expiresAt: '2026-12-31T09:00:00Z',
  revokedAt: isActive ? null : '2026-06-03T09:00:00Z',
  isActive,
  userId,
  userName,
});

/** Serves every user's keys and records each list request's user filter. */
function serveKeys(items: ReturnType<typeof key>[]) {
  const userFilters: (string | null)[] = [];
  mswServer.use(
    http.get('*/beacon/api/api-keys/admin', ({ request }) => {
      userFilters.push(new URL(request.url).searchParams.get('userId'));
      return HttpResponse.json({ totalCount: items.length, pageCount: 1, items });
    }),
  );
  return userFilters;
}

describe('AllApiKeysCard', () => {
  beforeEach(cleanup);

  it('revokes an active key after confirmation, and offers no revoke for an inactive one', async () => {
    serveKeys([key(2, 'Reporting', 5, 'bob', true), key(3, 'Old CI', 6, 'carol', false)]);
    const revoked: string[] = [];
    mswServer.use(
      csrf,
      http.delete('*/beacon/api/api-keys/admin/:id', ({ params }) => {
        revoked.push(String(params.id));
        return new HttpResponse(null, { status: 204 });
      }),
    );
    renderWithProviders(<AllApiKeysCard />);
    await screen.findByText('Reporting');

    expect(screen.getAllByRole('button', { name: 'Revoke' })).toHaveLength(1);
    expect(screen.getByText('Revoked')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('bob')).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Revoke key' }));

    await waitFor(() => expect(revoked).toEqual(['2']));
  });

  it("filters to one owner's keys when the owner is clicked", async () => {
    const userFilters = serveKeys([key(2, 'Reporting', 5, 'bob', true)]);
    renderWithProviders(<AllApiKeysCard />);

    fireEvent.click(await screen.findByRole('button', { name: 'bob' }));

    await waitFor(() => expect(userFilters).toContain('5'));
    expect(userFilters[0]).toBeNull();
    expect(await screen.findByRole('button', { name: 'Show all users' })).toBeInTheDocument();
  });

  it('shows the error when the keys cannot be loaded', async () => {
    mswServer.use(http.get('*/beacon/api/api-keys/admin', () => new HttpResponse(null, { status: 500 })));
    renderWithProviders(<AllApiKeysCard />);

    expect(await screen.findByText('Failed to load API keys')).toBeInTheDocument();
  });
});
