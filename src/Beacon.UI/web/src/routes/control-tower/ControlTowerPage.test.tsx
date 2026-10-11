import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { HealthStatus, NotificationStatus } from '@/lib/enums';
import ControlTowerPage from './ControlTowerPage';

function healthRow(overrides: Record<string, unknown>) {
  return {
    subscriptionId: 1,
    queryName: 'orders-overdue',
    dataSourceName: 'ledger-db',
    folderPath: null,
    healthStatus: HealthStatus.Green,
    totalExecutions: 10,
    successfulExecutions: 10,
    failedExecutions: 0,
    successRate: 100,
    lastExecutionTime: '2026-10-10T08:00:00Z',
    lastExecutionStatus: NotificationStatus.NotificationSent,
    lastResultCount: 3,
    ownerNotAuthorized: false,
    unresolvedTaskCount: 0,
    totalTaskCount: 0,
    anomalyCount30Days: 0,
    anomalySparkline: [],
    isActive: true,
    createTasks: false,
    storeResults: false,
    hasAnomalyDetection: false,
    aiActorId: null,
    aiActorName: null,
    ...overrides,
  };
}

function serve(rows: Array<Record<string, unknown>>, ownerNotAuthorizedContracts = 0) {
  mswServer.use(
    http.get('*/beacon/api/control-tower/health', () =>
      HttpResponse.json({ items: rows, totalCount: rows.length, pageCount: 1 }),
    ),
    http.get('*/beacon/api/control-tower/statistics', () =>
      HttpResponse.json({
        statistics: {
          totalSubscriptions: rows.length,
          healthySubscriptions: 1,
          warningSubscriptions: 1,
          criticalSubscriptions: 0,
          stalledSubscriptions: 0,
          totalUnresolvedTasks: 0,
          totalAnomalies30Days: 0,
          overallSuccessRate: 80,
          timeRangeDays: 30,
          ownerNotAuthorizedContracts,
        },
      }),
    ),
    http.get('*/beacon/api/query-folders', () => HttpResponse.json({ folders: [] })),
    http.get('*/beacon/api/control-tower/subscriptions/2/detail', () =>
      HttpResponse.json({
        detail: {
          subscriptionId: 2,
          queryName: 'revenue-join',
          queryId: 9,
          folderPath: null,
          cronExpression: '0 7 * * *',
          timeRangeDays: 30,
          recentExecutions: [
            {
              executionId: 77,
              createdTime: '2026-10-10T07:00:00Z',
              notificationStatus: NotificationStatus.Skipped,
              resultCount: 0,
              executionTimeMs: 0,
              errorMessage: 'Skipped: owner not authorized.',
            },
          ],
          openTasks: [],
          recentAnomalies: [],
        },
      }),
    ),
  );
}

describe('ControlTowerPage', () => {
  it('says how many data contracts are not being evaluated because their owner may no longer run them', async () => {
    serve([healthRow({})], 2);

    renderWithProviders(<ControlTowerPage />, { initialEntries: ['/control-tower'] });

    expect(await screen.findByText('2 data contracts are not being evaluated')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Data quality' })).toHaveAttribute('href', '/data-quality');
  });

  it('shows no contract warning while every contract is evaluated', async () => {
    serve([healthRow({})]);

    renderWithProviders(<ControlTowerPage />, { initialEntries: ['/control-tower'] });

    expect(await screen.findByText('orders-overdue')).toBeInTheDocument();
    expect(screen.queryByText(/not being evaluated/)).not.toBeInTheDocument();
  });

  it('flags a subscription whose owner may no longer run it, and labels its skipped last run', async () => {
    serve([
      healthRow({}),
      healthRow({
        subscriptionId: 2,
        queryName: 'revenue-join',
        healthStatus: HealthStatus.Amber,
        lastExecutionStatus: NotificationStatus.Skipped,
        ownerNotAuthorized: true,
      }),
    ]);

    renderWithProviders(<ControlTowerPage />, { initialEntries: ['/control-tower'] });

    expect(await screen.findByText('revenue-join')).toBeInTheDocument();
    // Only the row whose owner is not authorized carries the flag.
    expect(screen.getAllByText('Owner not authorized')).toHaveLength(1);
    expect(screen.getByText('Skipped')).toBeInTheDocument();
    expect(screen.getByText('Sent')).toBeInTheDocument();
  });

  it('explains the flag in the subscription panel and labels the skipped execution', async () => {
    serve([
      healthRow({
        subscriptionId: 2,
        queryName: 'revenue-join',
        healthStatus: HealthStatus.Amber,
        lastExecutionStatus: NotificationStatus.Skipped,
        ownerNotAuthorized: true,
      }),
    ]);

    renderWithProviders(<ControlTowerPage />, { initialEntries: ['/control-tower'] });

    fireEvent.click(await screen.findByText('revenue-join'));
    const panel = await screen.findByRole('dialog', { name: 'revenue-join' });

    expect(within(panel).getByText('Owner not authorized')).toBeInTheDocument();
    expect(within(panel).getByText(/scheduled runs are skipped/i)).toBeInTheDocument();
    expect(await within(panel).findByText('Skipped')).toBeInTheDocument();
    expect(within(panel).getByText('Skipped: owner not authorized.')).toBeInTheDocument();
  });
});
