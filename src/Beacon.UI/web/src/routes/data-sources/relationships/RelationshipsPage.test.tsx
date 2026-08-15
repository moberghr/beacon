import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../../vitest.setup';
import { Route, Routes } from 'react-router-dom';
import { renderWithProviders } from '@/test/render';
import RelationshipsPage from './RelationshipsPage';

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
  relationships: [
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
    },
  ],
};

function mockEndpoints(
  relationships: typeof RELATIONSHIPS | { relationships: [] } = RELATIONSHIPS,
  health: typeof HEALTH = HEALTH,
) {
  mswServer.use(
    http.get('*/beacon/api/data-sources/:id/relationships', () => HttpResponse.json(relationships)),
    http.get('*/beacon/api/data-sources/:id/schema-health', () => HttpResponse.json(health)),
  );
}

describe('RelationshipsPage', () => {
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
    mockEndpoints({ relationships: [] }, { ...HEALTH, relationshipCount: 0 });
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
});
