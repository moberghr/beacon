import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import QueryFoldersPage from './QueryFoldersPage';

const folder = {
  id: 5,
  name: 'Finance',
  description: null,
  parentFolderId: null,
  path: 'Finance',
  sortOrder: 0,
  queryCount: 2,
  childFolderCount: 0,
};

function folders(items: unknown[], seen?: (url: URL) => void) {
  return http.get('*/beacon/api/query-folders/paged', ({ request }) => {
    seen?.(new URL(request.url));
    return HttpResponse.json({ items, totalCount: items.length, pageCount: items.length ? 1 : 0 });
  });
}

describe('QueryFoldersPage', () => {
  it('lists one server page of folders with their paths and query counts, with search sent to the server', async () => {
    const urls: URL[] = [];
    mswServer.use(folders([folder], u => urls.push(u)));

    renderWithProviders(<QueryFoldersPage />, { initialEntries: ['/query-folders?search=fin'] });

    expect((await screen.findAllByText('Finance'))[0]).toBeInTheDocument();
    expect(urls[0].searchParams.get('search')).toBe('fin');
    expect(urls[0].searchParams.get('pageSize')).toBe('20');
  });

  it('creates a folder from the dialog', async () => {
    let body: unknown;
    mswServer.use(
      folders([]),
      http.post('*/beacon/api/query-folders', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ folderId: 9, path: 'Reports' }, { status: 201 });
      }),
    );

    renderWithProviders(<QueryFoldersPage />);

    expect(await screen.findByText('No folders yet')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /add folder/i }));
    fireEvent.change(await screen.findByLabelText(/^Name/), { target: { value: ' Reports ' } });
    fireEvent.click(screen.getByRole('button', { name: /create folder/i }));

    await waitFor(() => expect(body).toEqual({ name: 'Reports', description: null, parentFolderId: null }));
  });

  it('refuses a blank name without calling the server', async () => {
    let posted = false;
    mswServer.use(folders([]), http.post('*/beacon/api/query-folders', () => { posted = true; return HttpResponse.json({}); }));

    renderWithProviders(<QueryFoldersPage />);

    fireEvent.click(await screen.findByRole('button', { name: /add folder/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create folder/i }));

    expect(await screen.findByText('Name is required')).toBeInTheDocument();
    expect(posted).toBe(false);
  });

  it('renames a folder from its row', async () => {
    let request: { id: string; body: unknown } | undefined;
    mswServer.use(
      folders([folder]),
      http.put('*/beacon/api/query-folders/:id', async ({ params, request: r }) => {
        request = { id: String(params.id), body: await r.json() };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<QueryFoldersPage />);

    fireEvent.click((await screen.findAllByText('Finance'))[0]);
    const name = await screen.findByLabelText(/^Name/);
    expect(name).toHaveValue('Finance');
    fireEvent.change(name, { target: { value: 'Accounting' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => expect(request).toEqual({ id: '5', body: { name: 'Accounting', description: null } }));
  });

  it('deletes only after confirmation and can move the queries to the parent', async () => {
    let url: URL | undefined;
    mswServer.use(
      folders([folder]),
      http.delete('*/beacon/api/query-folders/:id', ({ request }) => {
        url = new URL(request.url);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<QueryFoldersPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Finance' }));
    expect(url).toBeUndefined();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(url?.searchParams.get('moveQueriesToParent')).toBe('true'));
    expect(url?.pathname).toBe('/beacon/api/query-folders/5');
  });
});
