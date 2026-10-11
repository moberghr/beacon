import { Routes, Route } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import DataContractDetailPage from './DataContractDetailPage';
import { renderWithProviders } from '@/test/render';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

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
  hasOwner: false,
  ownerDisplayName: null,
  ownerNotAuthorized: false,
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

  function serveContract(contract: Record<string, unknown>, roles: string[] = []) {
    mswServer.use(
      http.get('*/beacon/api/data-quality/contracts/7', () => HttpResponse.json(contract)),
      http.get('*/beacon/api/data-quality/contracts/7/evaluations', () => HttpResponse.json({ evaluations: [] })),
      http.get('*/beacon/api/auth/me', () =>
        HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: null, isAuthenticated: true, roles }),
      ),
    );
  }

  function renderContract() {
    renderWithProviders(
      <Routes>
        <Route path="/data-quality/:id" element={<DataContractDetailPage />} />
      </Routes>,
      { initialEntries: ['/data-quality/7'] },
    );
  }

  it('names the owner scheduled evaluations run as, or says none is recorded', async () => {
    serveContract({ ...SAMPLE_CONTRACT, hasOwner: true, ownerDisplayName: 'Ada Lovelace' });
    renderContract();
    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByText(/scheduled evaluations run as the owner/i)).toBeInTheDocument();
  });

  it('shows a neutral label for a contract without an owner', async () => {
    serveContract(SAMPLE_CONTRACT);
    renderContract();
    expect(await screen.findByText('No owner recorded')).toBeInTheDocument();
  });

  it('warns an Admin that scheduled evaluations are skipped, and offers to change the owner', async () => {
    serveContract({ ...SAMPLE_CONTRACT, hasOwner: true, ownerDisplayName: 'Ada Lovelace', ownerNotAuthorized: true }, ['Admin']);
    renderContract();

    const title = await screen.findByText('Owner not authorized: scheduled evaluations are skipped');
    // The banner: its title sits in its text column, beside the action.
    const banner = title.parentElement!.parentElement!;
    fireEvent.click(within(banner).getByRole('button', { name: 'Change owner' }));

    const dialog = await screen.findByRole('dialog', { name: "Change the data contract's owner" });
    expect(within(dialog).getByText(/must be able to run this contract's rules/)).toBeInTheDocument();
  });

  it('tells a non-Admin the evaluations are skipped until an Admin assigns an owner, without the action', async () => {
    serveContract({ ...SAMPLE_CONTRACT, hasOwner: true, ownerDisplayName: 'Ada Lovelace', ownerNotAuthorized: true });
    renderContract();

    expect(await screen.findByText('Owner not authorized: scheduled evaluations are skipped')).toBeInTheDocument();
    expect(screen.getByText(/until an Admin assigns an owner who has access/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change owner' })).not.toBeInTheDocument();
  });

  it('shows no warning while scheduled evaluations run', async () => {
    serveContract({ ...SAMPLE_CONTRACT, hasOwner: true, ownerDisplayName: 'Ada Lovelace' }, ['Admin']);
    renderContract();

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
    expect(screen.queryByText(/scheduled evaluations are skipped/)).not.toBeInTheDocument();
  });

  it("keeps the server's reason in the edit dialog when the contract is refused (400)", async () => {
    vi.mocked(toast.error).mockClear();
    const reason =
      "Table public.orders is not in the data source's metadata: a rule may only name the data source's own tables. Refresh the data source's metadata if the table is new.";
    serveContract(SAMPLE_CONTRACT);
    mswServer.use(
      http.get('*/beacon/api/data-sources/1', () =>
        HttpResponse.json({ id: 1, name: 'analytics-db', dataSourceType: 'Database', databaseEngineType: 'PostgreSQL' }),
      ),
      http.put('*/beacon/api/data-quality/contracts/7', () =>
        HttpResponse.json({ type: '/errors/invalid-operation', title: reason, status: 400 }, { status: 400 }),
      ),
    );
    renderContract();

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    const update = await within(dialog).findByRole('button', { name: 'Update' });
    await waitFor(() => expect(update).toBeEnabled());
    fireEvent.click(update);

    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent('The contract was not updated');
    expect(alert).toHaveTextContent(reason);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(toast.error).not.toHaveBeenCalled();
  });
});
