import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import MigrationJobsListPage from '@/routes/migration-jobs/MigrationJobsListPage';
import MigrationJobDetailPage from '@/routes/migration-jobs/MigrationJobDetailPage';
import RecipientsListPage from '@/routes/recipients/RecipientsListPage';
import DataContractDetailPage from '@/routes/data-quality/DataContractDetailPage';
import { RecipientsTab } from '@/routes/subscriptions/parts/RecipientsTab';
import type { SubscriptionDetail } from '@/routes/subscriptions/queries';
import DataSourcesListPage from '@/routes/data-sources/DataSourcesListPage';
import { AwaitingApprovalBanner } from './AwaitingApproval';
import { ApprovalStatus, ChangeApprovalKind } from '@/lib/enums';

const JOB = {
  id: 4,
  name: 'Nightly copy',
  description: 'copies orders',
  dataSourceId: 3,
  dataSourceName: 'warehouse',
  destinationDataSourceId: 5,
  destinationDataSourceName: 'reporting',
  destinationTable: 'orders_copy',
  mode: 1,
  isEnabled: true,
  schedule: null,
  createdTime: '2026-10-13T10:00:00Z',
  awaitingApproval: true,
  pendingApprovalRequestId: 31,
};

// A contract with a volume rule and a Custom SQL rule that waits for approval.
const CONTRACT = {
  id: 7,
  dataSourceId: 1,
  dataSourceName: 'analytics-db',
  schemaName: 'public',
  tableName: 'orders',
  name: 'Orders health check',
  description: null,
  cronExpression: '0 */6 * * *',
  isEnabled: true,
  hasOwner: true,
  ownerDisplayName: 'Ada',
  ownerNotAuthorized: false,
  alertOnFailure: true,
  failureThresholdScore: 80,
  createdTime: '2026-04-01T10:00:00Z',
  latestScore: 64,
  pendingApprovalRequestId: 12,
  rules: [
    { id: 1, name: 'Min row count', description: null, ruleType: 2, columnName: null, configuration: '{"minRows": 100}', severity: 2, weight: 1, isEnabled: true, awaitingApproval: false },
    { id: 2, name: 'Custom check', description: null, ruleType: 8, columnName: null, configuration: '{"sql":"SELECT 1 AS passed"}', severity: 2, weight: 1, isEnabled: true, awaitingApproval: true },
  ],
  recipients: [],
};

function renderContract() {
  renderWithProviders(
    <Routes>
      <Route path="/data-quality/:id" element={<DataContractDetailPage />} />
    </Routes>,
    { initialEntries: ['/data-quality/7'] },
  );
}

const noRuns = http.get('*/beacon/api/migrations/executions', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }));

describe('pages that show what waits for approval', () => {
  it('marks a migration job awaiting approval in the list, linking an Approver to the pending change', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['MigrationOperator', 'Approver'] }),
      http.get('*/beacon/api/migrations/jobs', () => HttpResponse.json({ items: [JOB, { ...JOB, id: 5, name: 'Approved copy', awaitingApproval: false, pendingApprovalRequestId: null }], totalCount: 2, pageCount: 1 })),
    );

    renderWithProviders(<MigrationJobsListPage />);

    const row = (await screen.findByText('Nightly copy')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Awaiting approval')).toBeInTheDocument();
    await waitFor(() => expect(within(row).getByRole('link', { name: 'Review the pending change' })).toHaveAttribute('href', '/approvals/changes/31'));
    const other = screen.getByText('Approved copy').closest('[role="row"]') as HTMLElement;
    expect(within(other).queryByText('Awaiting approval')).not.toBeInTheDocument();
  });

  it("says on a waiting job's page that it does not run, and does not offer the run", async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['MigrationOperator'] }),
      http.get('*/beacon/api/migrations/jobs/4', () => HttpResponse.json(JOB)),
      noRuns,
    );

    renderWithProviders(
      <Routes>
        <Route path="/migration-jobs/:id" element={<MigrationJobDetailPage />} />
      </Routes>,
      { initialEntries: ['/migration-jobs/4'] },
    );

    expect(await screen.findByText('Awaiting approval: this job does not run')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run now' })).toBeDisabled();
    // Someone who is not an Approver may open the request of a job they read, but does not decide it.
    expect(await screen.findByRole('button', { name: 'View the change' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Review the change' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View the change' })).toHaveAttribute('href', '/approvals/changes/31');
  });

  it('marks a recipient whose destination waits for approval', async () => {
    mswServer.use(
      permissionsHandler({ permissions: [] }),
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({
          totalCount: 2,
          pageCount: 1,
          items: [
            { id: 1, name: 'Ops channel', description: null, destination: null, notificationType: 4, headersJson: null, bodyTemplate: null, subscriptionCount: 2, awaitingApproval: true, pendingApprovalRequestId: 9 },
            { id: 2, name: 'Risk inbox', description: null, destination: null, notificationType: 2, headersJson: null, bodyTemplate: null, subscriptionCount: 1, awaitingApproval: false, pendingApprovalRequestId: null },
          ],
        }),
      ),
    );

    renderWithProviders(<RecipientsListPage />);

    const row = (await screen.findByText('Ops channel')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Awaiting approval')).toBeInTheDocument();
    expect(within(row).queryByRole('link')).not.toBeInTheDocument();
    const other = screen.getByText('Risk inbox').closest('[role="row"]') as HTMLElement;
    expect(within(other).queryByText('Awaiting approval')).not.toBeInTheDocument();
  });

  it("marks a contract's Custom SQL rule awaiting approval, and says its rules are not evaluated", async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['Approver'] }),
      http.get('*/beacon/api/data-quality/contracts/7', () =>
        HttpResponse.json({
          id: 7,
          dataSourceId: 1,
          dataSourceName: 'analytics-db',
          schemaName: 'public',
          tableName: 'orders',
          name: 'Orders health check',
          description: null,
          cronExpression: '0 */6 * * *',
          isEnabled: true,
          hasOwner: true,
          ownerDisplayName: 'Ada',
          ownerNotAuthorized: false,
          alertOnFailure: true,
          failureThresholdScore: 80,
          createdTime: '2026-04-01T10:00:00Z',
          latestScore: 92,
          pendingApprovalRequestId: 12,
          rules: [
            { id: 1, name: 'Min row count', description: null, ruleType: 2, columnName: null, configuration: '{"minRows": 100}', severity: 2, weight: 1, isEnabled: true, awaitingApproval: false },
            { id: 2, name: 'Custom check', description: null, ruleType: 8, columnName: null, configuration: '{"sql":"SELECT 1 AS passed"}', severity: 2, weight: 1, isEnabled: true, awaitingApproval: true },
          ],
          recipients: [],
        }),
      ),
      http.get('*/beacon/api/data-quality/contracts/7/evaluations', () => HttpResponse.json({ evaluations: [] })),
    );

    renderWithProviders(
      <Routes>
        <Route path="/data-quality/:id" element={<DataContractDetailPage />} />
      </Routes>,
      { initialEntries: ['/data-quality/7'] },
    );

    expect(await screen.findByText('Custom SQL awaiting approval: those rules are not evaluated')).toBeInTheDocument();
    const custom = screen.getByText('Custom check').closest('tr') as HTMLElement;
    expect(within(custom).getByText('Awaiting approval')).toBeInTheDocument();
    const volume = screen.getByText('Min row count').closest('tr') as HTMLElement;
    expect(within(volume).queryByText('Awaiting approval')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Review the change' })).toBeInTheDocument();
  });

  it("marks on a subscription a recipient its deliveries skip until its destination is approved", () => {
    const subscription = {
      id: 42,
      recipients: [
        { id: 7, name: 'Ops', description: null, destination: null, notificationType: 2, awaitingApproval: true },
        { id: 8, name: 'Risk', description: null, destination: null, notificationType: 2, awaitingApproval: false },
      ],
    } as unknown as SubscriptionDetail;

    renderWithProviders(<RecipientsTab subscription={subscription} canWrite isAdmin={false} />);

    expect(screen.getAllByText('Awaiting approval')).toHaveLength(1);
    expect(screen.getByText('Deliveries skip this recipient until its destination is approved.')).toBeInTheDocument();
  });
});

describe('pages that show a rejected change and what to do about it', () => {
  const REJECTED = { requestId: 30, status: ApprovalStatus.Rejected, comment: 'Writes into the finance schema', decidedAt: '2026-10-13T11:00:00Z' };

  it('marks a rejected migration job in the list with the remedy and the comment', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['MigrationOperator'] }),
      http.get('*/beacon/api/migrations/jobs', () =>
        HttpResponse.json({ items: [{ ...JOB, pendingApprovalRequestId: null, lastApprovalDecision: REJECTED }], totalCount: 1, pageCount: 1 }),
      ),
    );

    renderWithProviders(<MigrationJobsListPage />);

    const row = (await screen.findByText('Nightly copy')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Rejected')).toBeInTheDocument();
    expect(within(row).getByText('Rejected — delete and recreate the job: “Writes into the finance schema”')).toBeInTheDocument();
    await waitFor(() => expect(within(row).getByRole('link', { name: 'Open the rejected change' })).toHaveAttribute('href', '/approvals/changes/30'));
  });

  it("says on a rejected job's page to delete and recreate it, with the reviewer's comment", async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['MigrationOperator'] }),
      http.get('*/beacon/api/migrations/jobs/4', () => HttpResponse.json({ ...JOB, pendingApprovalRequestId: null, lastApprovalDecision: REJECTED })),
      noRuns,
    );

    renderWithProviders(
      <Routes>
        <Route path="/migration-jobs/:id" element={<MigrationJobDetailPage />} />
      </Routes>,
      { initialEntries: ['/migration-jobs/4'] },
    );

    expect(await screen.findByText('Rejected — delete and recreate the job')).toBeInTheDocument();
    expect(screen.getByText(/so it never runs as it is/)).toBeInTheDocument();
    expect(screen.getByText('Reviewer comment: “Writes into the finance schema”')).toBeInTheDocument();
    expect(screen.queryByText('Awaiting approval: this job does not run')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run now' })).toBeDisabled();
    expect(await screen.findByRole('link', { name: 'View the change' })).toHaveAttribute('href', '/approvals/changes/30');
  });

  it('marks a rejected recipient with the remedy and the comment, without a link for someone who is not an Approver', async () => {
    mswServer.use(
      permissionsHandler({ permissions: [] }),
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [
            { id: 1, name: 'Ops channel', description: null, destination: null, notificationType: 4, headersJson: null, bodyTemplate: null, subscriptionCount: 2, awaitingApproval: true, pendingApprovalRequestId: null, lastApprovalDecision: { ...REJECTED, comment: 'Not our workspace' } },
          ],
        }),
      ),
    );

    renderWithProviders(<RecipientsListPage />);

    const row = (await screen.findByText('Ops channel')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Rejected')).toBeInTheDocument();
    expect(within(row).getByText('Rejected — change it and save again: “Not our workspace”')).toBeInTheDocument();
    expect(within(row).queryByRole('link')).not.toBeInTheDocument();
  });

  it("says a contract's Custom SQL change was rejected, with the comment", async () => {
    mswServer.use(
      permissionsHandler({ permissions: [] }),
      http.get('*/beacon/api/data-quality/contracts/7', () =>
        HttpResponse.json({ ...CONTRACT, pendingApprovalRequestId: null, lastApprovalDecision: { ...REJECTED, comment: 'Scans the whole table' } }),
      ),
      http.get('*/beacon/api/data-quality/contracts/7/evaluations', () => HttpResponse.json({ evaluations: [] })),
    );

    renderContract();

    expect(await screen.findByText('Rejected — change it and save again')).toBeInTheDocument();
    expect(screen.getByText('Reviewer comment: “Scans the whole table”')).toBeInTheDocument();
    const custom = screen.getByText('Custom check').closest('tr') as HTMLElement;
    expect(within(custom).getByText('Rejected')).toBeInTheDocument();
  });

  it('says on a subscription that a recipient\'s change was rejected, with the comment', () => {
    const subscription = {
      id: 42,
      recipients: [
        { id: 7, name: 'Ops', description: null, destination: null, notificationType: 2, awaitingApproval: true, pendingApprovalRequestId: null, lastApprovalDecision: { ...REJECTED, comment: 'Unknown inbox' } },
      ],
    } as unknown as SubscriptionDetail;

    renderWithProviders(<RecipientsTab subscription={subscription} canWrite isAdmin={false} />);

    expect(screen.getByText('Rejected')).toBeInTheDocument();
    expect(screen.getByText(/Rejected — change it and save again: deliveries skip this recipient\./)).toBeInTheDocument();
    expect(screen.getByText('Reviewer comment: “Unknown inbox”')).toBeInTheDocument();
  });

  it('links an Approver to the pending change of a subscription\'s recipient', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));
    const subscription = {
      id: 42,
      recipients: [{ id: 7, name: 'Ops', description: null, destination: null, notificationType: 2, awaitingApproval: true, pendingApprovalRequestId: 12, lastApprovalDecision: null }],
    } as unknown as SubscriptionDetail;

    renderWithProviders(<RecipientsTab subscription={subscription} canWrite isAdmin={false} />);

    expect(await screen.findByRole('link', { name: 'Review the pending change' })).toHaveAttribute('href', '/approvals/changes/12');
    expect(screen.getByText('Deliveries skip this recipient until its destination is approved.')).toBeInTheDocument();
  });
});

describe('the recipients page for those who change recipients', () => {
  it('shows the fingerprints of where each recipient delivers and of its header values, to compare out of band', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['SecurityAdmin'] }),
      http.get('*/beacon/api/recipients', () =>
        HttpResponse.json({
          totalCount: 2,
          pageCount: 1,
          items: [
            {
              id: 1, name: 'Ops webhook', description: null, destination: 'https://hooks.example.com/ops/********', notificationType: 5,
              headersJson: '{"X-Api-Key":"********"}', bodyTemplate: null, subscriptionCount: 1,
              definitionFingerprint: '3f9a0c1d2e4b', headerFingerprints: [{ name: 'X-Api-Key', fingerprint: '7c2d9e0f1a3b' }],
            },
            {
              id: 2, name: 'Unreadable hook', description: null, destination: 'https://hooks.example.com/old/********', notificationType: 5,
              headersJson: null, bodyTemplate: null, subscriptionCount: 0, definitionFingerprint: null, headerFingerprints: null,
            },
          ],
        }),
      ),
    );

    renderWithProviders(<RecipientsListPage />);

    const fingerprints = await screen.findByLabelText('Fingerprints of Ops webhook');
    expect(within(fingerprints).getByText('3f9a0c1d2e4b')).toBeInTheDocument();
    expect(within(fingerprints).getByText('7c2d9e0f1a3b')).toBeInTheDocument();
    expect(within(fingerprints).getByText(/X-Api-Key/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Fingerprints of Unreadable hook')).not.toBeInTheDocument();
  });
});

describe('a contract evaluated while Custom SQL rules await approval', () => {
  it('marks the score partial, says how many rules were left out and how many alert recipients were held', async () => {
    mswServer.use(
      permissionsHandler({ permissions: [] }),
      http.get('*/beacon/api/data-quality/contracts/7', () => HttpResponse.json(CONTRACT)),
      http.get('*/beacon/api/data-quality/contracts/7/evaluations', () =>
        HttpResponse.json({
          evaluations: [
            {
              id: 50, dataContractId: 7, overallScore: 64, passedRules: 1, failedRules: 0, totalRules: 1, awaitingApprovalRules: 2,
              isPartial: true, heldAlertRecipients: 1, executionTimeMs: 12, createdTime: '2026-10-13T12:00:00Z',
              ruleResults: [{ id: 1, ruleName: 'Min row count', passed: true, score: 100, executionTimeMs: 3 }],
            },
            {
              id: 49, dataContractId: 7, overallScore: 95, passedRules: 2, failedRules: 0, totalRules: 2, awaitingApprovalRules: 0,
              isPartial: false, heldAlertRecipients: 0, executionTimeMs: 10, createdTime: '2026-10-12T12:00:00Z', ruleResults: [],
            },
          ],
        }),
      ),
    );

    renderContract();

    expect(await screen.findByText('the latest evaluation was partial and did not update it')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: /Evaluations/ }));
    const partialRow = (await screen.findByText('64.0%')).closest('tr') as HTMLElement;
    expect(within(partialRow).getByText('Partial')).toBeInTheDocument();
    expect(
      within(partialRow).getByText(
        'Partial: 2 Custom SQL rules awaiting approval were not evaluated; the alert skipped 1 recipient whose destination awaits approval.',
      ),
    ).toBeInTheDocument();
    const fullRow = screen.getByText('95.0%').closest('tr') as HTMLElement;
    expect(within(fullRow).queryByText('Partial')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: /Latest results/ }));
    expect(await screen.findByText('Partial evaluation: the score leaves out rules awaiting approval')).toBeInTheDocument();
  });
});

describe('a data source whose connection waits for approval, and rejected connection changes', () => {
  const HELD = {
    id: 9,
    name: 'ledger-db',
    dataSourceType: 'Database',
    databaseEngineType: 'PostgreSQL',
    queryCount: 0,
    migrationJobsCount: 0,
    metadataLoadingEnabled: true,
    openWhenUnlinked: false,
    projects: [],
    otherProjectCount: 0,
    approval: { requestId: 41, kind: ChangeApprovalKind.DataSourceConnection, requestedTime: '2026-10-13T10:00:00Z', awaitsFirstApproval: true },
  };
  const REJECTED = { requestId: 40, status: ApprovalStatus.Rejected, comment: 'Wrong replica', decidedAt: '2026-10-13T11:00:00Z' };

  it('marks the source in the list and links an Approver to the pending change', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['Approver'] }),
      http.get('*/beacon/api/data-sources', () => HttpResponse.json({ items: [HELD], totalCount: 1, pageCount: 1 })),
    );

    renderWithProviders(<DataSourcesListPage />);

    const row = (await screen.findByText('ledger-db')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Awaiting approval')).toHaveAttribute('title', expect.stringMatching(/makes no connection with the data source/));
    await waitFor(() => expect(within(row).getByRole('link', { name: 'Review the pending change' })).toHaveAttribute('href', '/approvals/changes/41'));
  });

  it('marks the requester\'s own waiting source without a link when they are not an Approver', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['DataSourceAdmin'] }),
      http.get('*/beacon/api/data-sources', () => HttpResponse.json({ items: [HELD], totalCount: 1, pageCount: 1 })),
    );

    renderWithProviders(<DataSourcesListPage />);

    const row = (await screen.findByText('ledger-db')).closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('Awaiting approval')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /new data source/i })).toBeInTheDocument();
    expect(within(row).queryByRole('link', { name: 'Review the pending change' })).not.toBeInTheDocument();
  });

  it('says what to do about a rejected connection, linking an Approver to the change', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(
      <AwaitingApprovalBanner kind={ChangeApprovalKind.DataSourceConnection} title="Held" sub="Held" requestId={null} lastDecision={REJECTED} />,
    );

    expect(screen.getByText('Rejected — replace the connection, or delete the data source')).toBeInTheDocument();
    expect(screen.getByText(/Beacon makes no connection with the data source until it holds an approved one/)).toBeInTheDocument();
    expect(screen.getByText('Reviewer comment: “Wrong replica”')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'View the change' })).toHaveAttribute('href', '/approvals/changes/40');
  });

  it('says a rejected LLM connection change leaves the stored connection in use, and links only an Admin to it', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(
      <AwaitingApprovalBanner kind={ChangeApprovalKind.LlmConnection} title="Pending" sub="Pending" requestId={null} lastDecision={REJECTED} />,
    );

    expect(screen.getByText('Rejected — the LLM connection stays as it was')).toBeInTheDocument();
    expect(screen.getByText(/the stored connection stays in use/)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('link', { name: 'View the change' })).not.toBeInTheDocument());

    mswServer.use(permissionsHandler({ isAdmin: true }));
    renderWithProviders(
      <AwaitingApprovalBanner kind={ChangeApprovalKind.LlmConnection} title="Pending" sub="Pending" requestId={null} lastDecision={REJECTED} />,
    );

    expect(await screen.findByRole('link', { name: 'View the change' })).toHaveAttribute('href', '/approvals/changes/40');
  });
});
