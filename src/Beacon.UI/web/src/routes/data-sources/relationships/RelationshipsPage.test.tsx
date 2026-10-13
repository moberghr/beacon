import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../../vitest.setup';
import { Route, Routes } from 'react-router-dom';
import { renderWithProviders } from '@/test/render';
import RelationshipsPage from './RelationshipsPage';
import { VERIFIED_DELETE_REFUSED } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

// The page reads :id via useParams, so it must be mounted behind its real route pattern.
function renderPage() {
  return renderWithProviders(
    <Routes>
      <Route path="/data-sources/:id/relationships" element={<RelationshipsPage />} />
    </Routes>,
    { initialEntries: ['/data-sources/1/relationships'] },
  );
}

const HEALTH = {
  tableCount: 12,
  relationshipCount: 3,
  verifiedRelationshipCount: 2,
  unverifiedRelationshipCount: 1,
  componentCount: 2,
  largestComponentSize: 9,
  isolatedTables: ['sales.audit_log'],
  junctionTables: ['sales.order_products'],
};

const RELATIONSHIPS = {
  items: [
    {
      id: 1,
      sourceSchema: 'sales',
      sourceTable: 'orders',
      sourceColumn: 'customer_id',
      targetSchema: 'sales',
      targetTable: 'customers',
      targetColumn: 'id',
      label: 'customer',
      origin: 0,
      cardinality: 2,
      confidence: 1,
      isVerified: true,
      verifiedTime: null,
      canCurate: true,
    },
    {
      id: 2,
      sourceSchema: 'billing',
      sourceTable: 'invoices',
      sourceColumn: 'account_id',
      targetSchema: 'crm',
      targetTable: 'accounts',
      targetColumn: 'id',
      label: 'account',
      origin: 1,
      cardinality: 2,
      confidence: 0.9,
      isVerified: false,
      verifiedTime: null,
      canCurate: true,
    },
  ],
};

function mockEndpoints(
  relationships: { items: unknown[]; totalCount?: number; pageCount?: number } = RELATIONSHIPS,
  health: typeof HEALTH = HEALTH,
) {
  mswServer.use(
    http.get('*/beacon/api/data-sources/:id/relationships', () => HttpResponse.json({ totalCount: relationships.items.length, pageCount: relationships.items.length ? 1 : 0, ...relationships })),
    http.get('*/beacon/api/data-sources/:id/schema-health', () => HttpResponse.json(health)),
  );
}

describe('RelationshipsPage', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('renders persisted relationships with their origin and verification state', async () => {
    mockEndpoints();
    renderPage();

    await screen.findByText(/sales\.orders\.customer_id/);
    expect(screen.getByText(/sales\.customers\.id/)).toBeInTheDocument();
    expect(screen.getByText('Foreign key')).toBeInTheDocument();
    expect(screen.getByText('Inferred')).toBeInTheDocument();
    expect(screen.getByText('Verified')).toBeInTheDocument();
    expect(screen.getByText(/Unverified · 0\.90/)).toBeInTheDocument();
  });

  it('shows an empty state and never invents rows when nothing is registered', async () => {
    mockEndpoints({ items: [], totalCount: 0, pageCount: 0 }, { ...HEALTH, relationshipCount: 0 });
    renderPage();

    await screen.findByText(/No relationships registered yet/i);
    expect(screen.queryByText(/sales\.orders/)).toBeNull();
  });

  it('reports disconnected groups and isolated tables in the health panel', async () => {
    mockEndpoints();
    renderPage();

    await screen.findByText(/splits into 2 disconnected groups/i);
    expect(screen.getByText('sales.audit_log')).toBeInTheDocument();
    expect(screen.getByText('sales.order_products')).toBeInTheDocument();
  });

  it('verifies an unverified relationship', async () => {
    mockEndpoints();
    let verifiedBody: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/data-sources/:id/relationships/:relationshipId/verify', async ({ request }) => {
        verifiedBody = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderPage();
    await screen.findByText(/billing\.invoices\.account_id/);

    fireEvent.click(screen.getByRole('button', { name: /verify relationship/i }));

    await waitFor(() => expect(verifiedBody).toEqual({ isVerified: true }));
  });

  it('does not let a caller who may not curate the source verify, and says why', async () => {
    mockEndpoints({
      items: RELATIONSHIPS.items.map(x => ({ ...x, canCurate: false })),
    });

    renderPage();
    await screen.findByText(/billing\.invoices\.account_id/);

    const verify = screen.getByRole('button', { name: /verify relationship/i });
    expect(verify).toBeDisabled();
    expect(verify).toHaveAttribute('title', expect.stringMatching(/project admin of every project linking this data source/i));
    expect(screen.getByRole('button', { name: /remove verification/i })).toBeDisabled();
  });

  it('says a relationship added by someone who may not curate waits for a curator', async () => {
    mockEndpoints();
    mswServer.use(
      http.post('*/beacon/api/data-sources/:id/relationships', () => HttpResponse.json({ id: 9, isVerified: false })),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getByRole('button', { name: /add relationship/i }));
    for (const [label, value] of [
      ['Source schema', 'hr'],
      ['Source table', 'employees'],
      ['Source column', 'department_id'],
      ['Target schema', 'hr'],
      ['Target table', 'departments'],
      ['Target column', 'id'],
    ]) {
      fireEvent.change(screen.getByLabelText(label), { target: { value } });
    }
    fireEvent.click(screen.getByRole('button', { name: /^add$/i }));

    await waitFor(() =>
      expect(toast.success).toHaveBeenCalledWith('Relationship added, pending verification by a curator'),
    );
  });

  it('says nothing more when the relationship added is verified', async () => {
    mockEndpoints();
    mswServer.use(
      http.post('*/beacon/api/data-sources/:id/relationships', () => HttpResponse.json({ id: 9, isVerified: true })),
      http.post('*/beacon/api/data-sources/:id/relationships/discover-preview', () =>
        HttpResponse.json({
          proposals: [
            {
              sourceSchema: 'hr',
              sourceTable: 'employees',
              sourceColumn: 'department_id',
              targetSchema: 'hr',
              targetTable: 'departments',
              targetColumn: 'id',
              label: 'department',
              origin: 1,
              cardinality: 2,
              confidence: 0.9,
            },
          ],
        }),
      ),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getByRole('button', { name: /discover/i }));
    await screen.findByText(/Discovered proposals \(1\)/);
    fireEvent.click(screen.getByRole('button', { name: /accept/i }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Relationship added'));
  });

  it('lists discovery proposals without saving them until accepted', async () => {
    mockEndpoints();
    let created: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/data-sources/:id/relationships/discover-preview', () =>
        HttpResponse.json({
          proposals: [
            {
              sourceSchema: 'hr',
              sourceTable: 'employees',
              sourceColumn: 'department_id',
              targetSchema: 'hr',
              targetTable: 'departments',
              targetColumn: 'id',
              label: 'department',
              origin: 1,
              cardinality: 2,
              confidence: 0.9,
            },
          ],
        }),
      ),
      http.post('*/beacon/api/data-sources/:id/relationships', async ({ request }) => {
        created = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);

    fireEvent.click(screen.getByRole('button', { name: /discover/i }));

    await screen.findByText(/Discovered proposals \(1\)/);
    expect(screen.getByText(/hr\.employees\.department_id/)).toBeInTheDocument();
    expect(created).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /accept/i }));

    await waitFor(() =>
      expect(created).toMatchObject({
        sourceTable: 'employees',
        targetTable: 'departments',
      }),
    );
  });

  it('sends a relabel and says when the changed relationship waits for a curator', async () => {
    mockEndpoints();
    let body: unknown = null;
    mswServer.use(
      http.put('*/beacon/api/data-sources/:id/relationships/:relationshipId', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ isVerified: false });
      }),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getAllByRole('button', { name: /edit relationship/i })[0]);
    fireEvent.change(screen.getByLabelText('Label'), { target: { value: 'buyer' } });
    fireEvent.change(screen.getByLabelText('Relationship cardinality'), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => expect(body).toEqual({ label: 'buyer', cardinality: 1 }));
    await waitFor(() =>
      expect(toast.success).toHaveBeenCalledWith('Relationship updated, pending curator verification'),
    );
    await waitFor(() => expect(screen.queryByRole('button', { name: /^save$/i })).toBeNull());
  });

  it('says nothing more when the changed relationship stays verified', async () => {
    mockEndpoints();
    mswServer.use(
      http.put('*/beacon/api/data-sources/:id/relationships/:relationshipId', () => HttpResponse.json({ isVerified: true })),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getAllByRole('button', { name: /edit relationship/i })[0]);
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Relationship updated'));
  });

  it('tells a caller who may not curate that changing a verified relationship unverifies it', async () => {
    mockEndpoints({ items: RELATIONSHIPS.items.map(x => ({ ...x, canCurate: false })) });

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getAllByRole('button', { name: /edit relationship/i })[0]);

    expect(screen.getByText(/Changing it sets it back to pending curator verification/)).toBeInTheDocument();
  });

  it('does not offer a caller who may not curate the deletion of a verified relationship, and says why', async () => {
    mockEndpoints({ items: RELATIONSHIPS.items.map(x => ({ ...x, canCurate: false })) });

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);

    const [verified, unverified] = screen.getAllByRole('button', { name: /delete relationship/i });
    expect(verified).toBeDisabled();
    expect(verified).toHaveAttribute('title', expect.stringMatching(/verified relationship is removed only by a project admin/i));
    expect(unverified).toBeEnabled();
  });

  it('explains a refused delete, which the server answers with a bare 403', async () => {
    mockEndpoints();
    mswServer.use(
      http.delete('*/beacon/api/data-sources/:id/relationships/:relationshipId', () =>
        HttpResponse.json(
          { title: 'Forbidden', status: 403 },
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        )),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getAllByRole('button', { name: /delete relationship/i })[0]);

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(VERIFIED_DELETE_REFUSED));
    expect(VERIFIED_DELETE_REFUSED).toMatch(/verified relationship needs the project admin role/);
  });

  it('shows the server reason for any other refused delete', async () => {
    mockEndpoints();
    mswServer.use(
      http.delete('*/beacon/api/data-sources/:id/relationships/:relationshipId', () =>
        HttpResponse.json(
          { title: 'Not Found', status: 404 },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        )),
    );

    renderPage();
    await screen.findByText(/sales\.orders\.customer_id/);
    fireEvent.click(screen.getAllByRole('button', { name: /delete relationship/i })[0]);

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Not Found'));
  });
});
