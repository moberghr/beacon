import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import GlossaryPage from './GlossaryPage';

const term = {
  id: 3,
  projectId: 1,
  dataSourceId: null,
  term: 'Active customer',
  synonyms: 'client',
  definition: 'Bought in the last 90 days',
  targetSchema: null,
  targetTable: 'customers',
  targetColumn: null,
  metricExpression: null,
  isActive: true,
  createdTime: '2026-01-01T00:00:00Z',
};

function glossary(items: unknown[], seen?: (url: URL) => void) {
  return http.get('*/beacon/api/glossary/paged', ({ request }) => {
    seen?.(new URL(request.url));
    return HttpResponse.json({ items, totalCount: items.length, pageCount: items.length ? 1 : 0 });
  });
}

describe('GlossaryPage', () => {
  it('asks for a project and loads nothing until one is chosen', async () => {
    const urls: URL[] = [];
    mswServer.use(permissionsHandler({ isAdmin: true }), glossary([term], u => urls.push(u)));

    renderWithProviders(<GlossaryPage />);

    expect(await screen.findByText('Choose a project')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /add term/i })).not.toBeInTheDocument();
    expect(urls).toHaveLength(0);
  });

  it('lists a project\'s terms from the server with the project, search and inactive filters', async () => {
    const urls: URL[] = [];
    mswServer.use(permissionsHandler({ isAdmin: true }), glossary([term], u => urls.push(u)));

    renderWithProviders(<GlossaryPage />, { initialEntries: ['/glossary?projectId=1&search=act&inactive=true'] });

    expect(await screen.findByText('Active customer')).toBeInTheDocument();
    expect(screen.getByText('Bought in the last 90 days')).toBeInTheDocument();
    const url = urls[0];
    expect(url.searchParams.get('projectId')).toBe('1');
    expect(url.searchParams.get('search')).toBe('act');
    expect(url.searchParams.get('includeInactive')).toBe('true');
    expect(url.searchParams.get('pageSize')).toBe('20');
  });

  it('creates a term in the chosen project', async () => {
    let body: Record<string, unknown> | undefined;
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      glossary([]),
      http.post('*/beacon/api/glossary', async ({ request }) => {
        body = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ id: 4 });
      }),
    );

    renderWithProviders(<GlossaryPage />, { initialEntries: ['/glossary?projectId=7'] });

    fireEvent.click(await screen.findByRole('button', { name: /add term/i }));
    fireEvent.change(await screen.findByLabelText(/^Term/), { target: { value: 'Churn' } });
    fireEvent.change(screen.getByLabelText(/^Definition/), { target: { value: 'Lost customers' } });
    fireEvent.click(screen.getByRole('button', { name: /create term/i }));

    await waitFor(() => expect(body).toBeDefined());
    expect(body).toMatchObject({ projectId: 7, term: 'Churn', definition: 'Lost customers', synonyms: null, targetTable: null });
  });

  it('requires a term and a definition', async () => {
    let posted = false;
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      glossary([]),
      http.post('*/beacon/api/glossary', () => { posted = true; return HttpResponse.json({ id: 1 }); }),
    );

    renderWithProviders(<GlossaryPage />, { initialEntries: ['/glossary?projectId=7'] });

    fireEvent.click(await screen.findByRole('button', { name: /add term/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create term/i }));

    expect(await screen.findByText('Term is required')).toBeInTheDocument();
    expect(screen.getByText('Definition is required')).toBeInTheDocument();
    expect(posted).toBe(false);
  });

  it('edits a term, sending an emptied optional field as blank so the server clears it', async () => {
    let request: { id: string; body: Record<string, unknown> } | undefined;
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      glossary([term]),
      http.put('*/beacon/api/glossary/:id', async ({ params, request: r }) => {
        request = { id: String(params.id), body: (await r.json()) as Record<string, unknown> };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<GlossaryPage />, { initialEntries: ['/glossary?projectId=1'] });

    fireEvent.click(await screen.findByText('Active customer'));
    fireEvent.change(await screen.findByLabelText(/^Target table/), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }));

    await waitFor(() => expect(request).toBeDefined());
    expect(request?.id).toBe('3');
    expect(request?.body).toMatchObject({ term: 'Active customer', targetTable: '', isActive: true });
  });

  it('deletes a term only after confirmation', async () => {
    let deleted: string | undefined;
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      glossary([term]),
      http.delete('*/beacon/api/glossary/:id', ({ params }) => {
        deleted = String(params.id);
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<GlossaryPage />, { initialEntries: ['/glossary?projectId=1'] });

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Active customer' }));
    expect(deleted).toBeUndefined();
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deleted).toBe('3'));
  });
});
