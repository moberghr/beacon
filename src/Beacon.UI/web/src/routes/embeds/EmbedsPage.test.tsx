import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import EmbedsPage from './EmbedsPage';

const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' }));

function grant(id: number, name: string, status: string) {
  return {
    id,
    publicId: `${id}`.padStart(32, '0'),
    name,
    projectId: 1,
    projectName: 'Acme Analytics',
    queryId: 7,
    queryName: 'Orders by region',
    queryVersionId: 30,
    versionNumber: 3,
    allowedOrigins: ['https://portal.example.com'],
    lockedParameters: ['region'],
    createdByLabel: 'Ana Admin',
    createdTime: '2026-10-01T09:00:00Z',
    expiresTime: '2026-12-30T09:00:00Z',
    revokedTime: status === 'revoked' ? '2026-10-02T09:00:00Z' : null,
    lastTokenIssuedTime: null,
    status,
  };
}

const grants = http.get('*/beacon/api/embed-grants', () =>
  HttpResponse.json({
    totalCount: 2,
    pageCount: 1,
    items: [grant(11, 'Partner orders', 'active'), grant(12, 'Old portal', 'revoked')],
  }),
);

describe('EmbedsPage', () => {
  it('lists the grants the API returns', async () => {
    mswServer.use(grants);

    renderWithProviders(<EmbedsPage />);

    expect(await screen.findByText('Partner orders')).toBeInTheDocument();
    expect(screen.getByText('Old portal')).toBeInTheDocument();
    expect(screen.getAllByText('https://portal.example.com')).toHaveLength(2);
    expect(screen.getAllByText('Ana Admin')).toHaveLength(2);
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Revoked')).toBeInTheDocument();
    expect(screen.getAllByText('Never')).toHaveLength(2);
    // Only a live grant can be revoked.
    expect(screen.getAllByRole('button', { name: 'Revoke' })).toHaveLength(1);
  });

  it('starts empty when there are no grants', async () => {
    mswServer.use(http.get('*/beacon/api/embed-grants', () => HttpResponse.json({ totalCount: 0, pageCount: 0, items: [] })));

    renderWithProviders(<EmbedsPage />);

    expect(await screen.findByText('No embed grants yet')).toBeInTheDocument();
  });

  it('revokes a grant only after the confirmation', async () => {
    const revoked: string[] = [];
    mswServer.use(
      csrf,
      grants,
      http.post('*/beacon/api/embed-grants/:id/revoke', ({ params }) => {
        revoked.push(String(params.id));
        return HttpResponse.json({ id: Number(params.id), revokedTime: '2026-10-24T09:00:00Z', revoked: true });
      }),
    );

    renderWithProviders(<EmbedsPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Every page framing it stops showing the view immediately/)).toBeInTheDocument();
    expect(revoked).toEqual([]);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Revoke grant' }));

    await waitFor(() => expect(revoked).toEqual(['11']));
  });

  it('revokes nothing when the confirmation is cancelled', async () => {
    const revoked: string[] = [];
    mswServer.use(
      grants,
      http.post('*/beacon/api/embed-grants/:id/revoke', ({ params }) => {
        revoked.push(String(params.id));
        return HttpResponse.json({});
      }),
    );

    renderWithProviders(<EmbedsPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(revoked).toEqual([]);
  });
});
