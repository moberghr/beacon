import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { MoveQueryDialog } from './MoveQueryDialog';

describe('MoveQueryDialog', () => {
  it('takes a query out of its folder when the choice is cleared', async () => {
    let sent: { id: string; body: unknown } | undefined;
    mswServer.use(
      http.get('*/beacon/api/query-folders/paged', () =>
        HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
      http.put('*/beacon/api/queries/:id/folder', async ({ params, request }) => {
        sent = { id: String(params.id), body: await request.json() };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(
      <MoveQueryDialog
        open
        onClose={() => {}}
        query={{ queryId: 12, name: 'Revenue', folderId: 5, folderPath: 'Finance' }}
      />,
    );

    expect(await screen.findByText('Finance')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /clear/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Move' }));

    await waitFor(() => expect(sent).toEqual({ id: '12', body: { folderId: null } }));
  });
});
