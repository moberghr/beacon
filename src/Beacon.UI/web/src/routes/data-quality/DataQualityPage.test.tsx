import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import DataQualityPage from './DataQualityPage';

function contract(overrides: Record<string, unknown>) {
  return {
    id: 1,
    dataSourceId: 9,
    dataSourceName: 'finance-db',
    schemaName: 'sales',
    tableName: 'orders',
    name: 'Orders are complete',
    description: null,
    cronExpression: '0 * * * *',
    isEnabled: true,
    hasOwner: true,
    ownerDisplayName: 'Ada Lovelace',
    ownerNotAuthorized: false,
    alertOnFailure: false,
    failureThresholdScore: 80,
    createdTime: '2026-10-01T06:00:00Z',
    latestScore: 95,
    rules: [],
    recipients: [],
    ...overrides,
  };
}

describe('DataQualityPage', () => {
  it('flags the contracts whose scheduled evaluations are skipped, and counts them per data source', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-quality/overview/paged', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [
          {
            dataSourceId: 9,
            dataSourceName: 'finance-db',
            averageScore: 90,
            totalTables: 2,
            healthyTables: 2,
            degradingTables: 0,
            activeContracts: 2,
            ownerNotAuthorizedContracts: 1,
            tableScores: [],
          },
          ],
        }),
      ),
      http.get('*/beacon/api/data-quality/contracts', () =>
        HttpResponse.json({
          totalCount: 2,
          pageCount: 1,
          items: [
            contract({}),
            contract({ id: 2, name: 'Refunds are complete', tableName: 'refunds', ownerNotAuthorized: true }),
          ],
        }),
      ),
    );

    renderWithProviders(<DataQualityPage />);

    const flagged = (await screen.findByText('Refunds are complete')).closest('tr')!;
    const healthy = screen.getByText('Orders are complete').closest('tr')!;
    expect(within(flagged).getByText('Owner not authorized')).toBeInTheDocument();
    expect(within(healthy).queryByText('Owner not authorized')).not.toBeInTheDocument();
    expect(screen.getByText('Skipped (owner)').previousElementSibling).toHaveTextContent('1');
  });

  it('pages the data source cards on the server', async () => {
    let asked = new URLSearchParams();
    mswServer.use(
      http.get('*/beacon/api/data-quality/overview/paged', ({ request }) => {
        asked = new URL(request.url).searchParams;
        return HttpResponse.json({
          totalCount: 30,
          pageCount: 3,
          items: [
            {
              dataSourceId: 9,
              dataSourceName: 'finance-db',
              averageScore: 90,
              totalTables: 2,
              healthyTables: 2,
              degradingTables: 0,
              activeContracts: 2,
              ownerNotAuthorizedContracts: 0,
              tableScores: [],
            },
          ],
        });
      }),
      http.get('*/beacon/api/data-quality/contracts', () =>
        HttpResponse.json({ totalCount: 0, pageCount: 0, items: [] }),
      ),
    );

    renderWithProviders(<DataQualityPage />);

    expect(await screen.findByText('finance-db')).toBeInTheDocument();
    expect(asked.get('page')).toBe('0');
    expect(asked.get('pageSize')).toBe('12');
  });
});
