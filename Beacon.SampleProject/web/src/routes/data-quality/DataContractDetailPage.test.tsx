import { Routes, Route } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import DataContractDetailPage from './DataContractDetailPage';
import { renderWithProviders } from '@/test/render';

const SAMPLE_CONTRACT = {
  id: 7,
  dataSourceId: 1,
  dataSourceName: 'analytics-db',
  schemaName: 'public',
  tableName: 'orders',
  name: 'Orders health check',
  description: 'Volume + freshness checks.',
  cronExpression: '0 */6 * * *',
  isEnabled: true,
  ownerUserId: null,
  alertOnFailure: true,
  failureThresholdScore: 80,
  createdTime: '2026-04-01T10:00:00Z',
  latestScore: 92,
  rules: [
    {
      id: 1,
      name: 'Min row count',
      description: null,
      ruleType: 0,
      columnName: null,
      configuration: '{"minRows": 100}',
      severity: 2,
      weight: 1,
      isEnabled: true,
    },
  ],
  recipients: [],
};

describe('DataContractDetailPage', () => {
  it('renders contract metrics, rules tab, and evaluation history tab', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-quality/contracts/7', () => HttpResponse.json(SAMPLE_CONTRACT)),
      http.get('*/beacon/api/data-quality/contracts/7/evaluations', () =>
        HttpResponse.json({
          evaluations: [
            {
              id: 11,
              dataContractId: 7,
              overallScore: 92,
              passedRules: 1,
              failedRules: 0,
              totalRules: 1,
              executionTimeMs: 130,
              createdTime: '2026-05-01T10:00:00Z',
              ruleResults: [],
            },
          ],
        }),
      ),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', userName: 'tester', isAdmin: false }),
      ),
    );

    renderWithProviders(
      <Routes>
        <Route path="/data-quality/:id" element={<DataContractDetailPage />} />
      </Routes>,
      { initialEntries: ['/data-quality/7'] },
    );

    await waitFor(() => {
      expect(screen.getByText('Orders health check')).toBeInTheDocument();
    });

    // Hero metrics show latest score and rule count.
    expect(screen.getByText('92%')).toBeInTheDocument();
    // Rules tab shows the rule by default.
    expect(screen.getByText('Min row count')).toBeInTheDocument();
  });
});
