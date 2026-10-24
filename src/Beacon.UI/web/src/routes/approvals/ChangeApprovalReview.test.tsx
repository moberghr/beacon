import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import {
  AiProvider,
  ApprovalStatus,
  BedrockAuthMode,
  ChangeApprovalKind,
  DatabaseEngineType,
  DataSourceType,
  MigrationMode,
  NotificationType,
} from '@/lib/enums';
import ApprovalsListPage from './ApprovalsListPage';
import ChangeApprovalDetailPage from './ChangeApprovalDetailPage';

const JOB = {
  sourceDataSourceId: 3,
  sourceDataSourceName: 'warehouse',
  queryText: 'SELECT id, total FROM orders',
  destinationDataSourceId: 5,
  destinationDataSourceName: 'reporting',
  destinationTable: 'orders_copy',
  mode: MigrationMode.Insert,
  transformationScript: null,
};

// A pending new migration job, requested by someone else.
const DETAIL = {
  id: 31,
  kind: ChangeApprovalKind.MigrationJob,
  resourceId: 4,
  resourceName: 'Nightly copy',
  resourceExists: true,
  status: ApprovalStatus.Pending,
  requestedByUserName: 'Ola Operator',
  reviewedByUserName: null,
  reviewedAt: null,
  reviewComment: null,
  changeSummary: 'Migration job created',
  createdTime: '2026-10-13T10:00:00Z',
  previousMigrationJob: null,
  proposedMigrationJob: JOB,
  previousCustomSqlRules: null,
  proposedCustomSqlRules: null,
  previousRecipientDestination: null,
  proposedRecipientDestination: null,
  payloadHash: 'hash-of-what-is-shown',
  isOwnRequest: false,
  mayApprove: true,
  approveBlockedReason: null,
  isStale: false,
};

function serve(detail: Record<string, unknown>, permissions: string[] = ['Approver']) {
  mswServer.use(
    permissionsHandler({ permissions: permissions as never[] }),
    http.get('*/beacon/api/approvals/changes/31', () => HttpResponse.json(detail)),
  );
}

function renderDetail() {
  renderWithProviders(
    <Routes>
      <Route path="/approvals/changes/:id" element={<ChangeApprovalDetailPage />} />
      <Route path="/approvals" element={<div>Approvals list</div>} />
    </Routes>,
    { initialEntries: ['/approvals/changes/31'] },
  );
}

describe('reviewing a change of a migration job, a Custom SQL rule or a recipient', () => {
  it("shows a new migration job's whole definition, with its data sources by name", async () => {
    serve(DETAIL);

    renderDetail();

    const definition = await screen.findByRole('region', { name: 'Migration job definition' });
    expect(within(definition).getByText(/The job is new/)).toBeInTheDocument();
    expect(within(definition).getByText('warehouse')).toBeInTheDocument();
    expect(within(definition).getByText('reporting')).toBeInTheDocument();
    expect(within(definition).getByText('orders_copy')).toBeInTheDocument();
    expect(within(definition).getByText('SELECT id, total FROM orders')).toBeInTheDocument();
  });

  it("shows a changed job's definition before and after", async () => {
    serve({ ...DETAIL, previousMigrationJob: { ...JOB, queryText: 'SELECT id FROM orders', destinationTable: 'orders_old' } });

    renderDetail();

    const definition = await screen.findByRole('region', { name: 'Migration job definition' });
    expect(within(definition).getByText('orders_old')).toBeInTheDocument();
    expect(within(definition).getByText('SELECT id FROM orders')).toBeInTheDocument();
    expect(within(definition).getByText('Current source query')).toBeInTheDocument();
    expect(within(definition).getByText('Proposed source query')).toBeInTheDocument();
  });

  it('approves the payload it showed: the payload hash of the detail goes with the approval', async () => {
    serve(DETAIL);
    let body: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/approvals/changes/31/approve', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDetail();
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(body).toEqual({ comment: null, expectedPayloadHash: 'hash-of-what-is-shown' }));
  });

  it('does not offer the requester their own approval, and says why; they may still withdraw it', async () => {
    serve({ ...DETAIL, isOwnRequest: true, mayApprove: false, approveBlockedReason: 'A change is approved by someone other than its requester.' });

    renderDetail();

    expect(await screen.findByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeEnabled();
    expect(screen.getByText('You cannot approve this request')).toBeInTheDocument();
    expect(screen.getByText('A change is approved by someone other than its requester.')).toBeInTheDocument();
  });

  it('says a request is stale, with the reason, and does not offer its approval', async () => {
    serve({ ...DETAIL, mayApprove: false, isStale: true, approveBlockedReason: 'What it changes was changed again after this request was made.' });

    renderDetail();

    expect(await screen.findByText('This changed again since the request was made')).toBeInTheDocument();
    expect(screen.getByText('What it changes was changed again after this request was made.')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.queryByText('You cannot approve this request')).not.toBeInTheDocument();
  });

  it('shows a superseded request without a decision', async () => {
    serve({ ...DETAIL, status: ApprovalStatus.Superseded, mayApprove: false });

    renderDetail();

    expect(await screen.findByText('A newer change replaced this request')).toBeInTheDocument();
    expect(screen.getByText('Superseded')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });

  it('shows an error view, no decision and no content when the server refuses the request as not found (an LLM change for a non-Admin)', async () => {
    mswServer.use(
      permissionsHandler({ permissions: ['Approver'] }),
      http.get('*/beacon/api/approvals/changes/31', () => HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })),
    );

    renderDetail();

    expect(await screen.findByText(/not found/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(screen.queryByText('Nightly copy')).not.toBeInTheDocument();
  });

  it('shows a deleted resource without a link', async () => {
    serve({ ...DETAIL, resourceExists: false, isStale: true, mayApprove: false, approveBlockedReason: 'What this request changes no longer exists.' });

    renderDetail();

    expect(await screen.findByText('What this request changes was deleted')).toBeInTheDocument();
    expect(screen.getByText('(deleted)')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Nightly copy' })).not.toBeInTheDocument();
  });

  it("shows a contract's Custom SQL rules: the waiting ones, the unchanged ones and the removed ones", async () => {
    serve({
      ...DETAIL,
      kind: ChangeApprovalKind.CustomSqlRules,
      resourceName: 'Orders contract',
      previousMigrationJob: null,
      proposedMigrationJob: null,
      previousCustomSqlRules: {
        dataSourceId: 3,
        dataSourceName: 'warehouse',
        rules: [
          { name: 'counts', sql: 'SELECT 1 AS passed', awaitsApproval: false },
          { name: 'nulls', sql: 'SELECT 0 AS passed', awaitsApproval: false },
          { name: 'old', sql: 'SELECT 2 AS passed', awaitsApproval: false },
        ],
      },
      proposedCustomSqlRules: {
        dataSourceId: 3,
        dataSourceName: 'warehouse',
        rules: [
          { name: 'counts', sql: 'SELECT 1 AS passed', awaitsApproval: false },
          { name: 'nulls', sql: 'SELECT 1 AS passed WHERE id IS NULL', awaitsApproval: true },
        ],
      },
    });

    renderDetail();

    const counts = await screen.findByRole('region', { name: 'Rule counts' });
    expect(within(counts).getByText('Unchanged')).toBeInTheDocument();
    const nulls = screen.getByRole('region', { name: 'Rule nulls' });
    expect(within(nulls).getByText('Awaits approval')).toBeInTheDocument();
    expect(within(nulls).getByText('SELECT 0 AS passed')).toBeInTheDocument();
    expect(within(nulls).getByText('SELECT 1 AS passed WHERE id IS NULL')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Rule old' })).getByText('Removed')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Orders contract' })).toHaveAttribute('href', '/data-quality/4');
  });

  it("shows what identifies a recipient's destination, its header names with fingerprints and the definition's fingerprint", async () => {
    serve({
      ...DETAIL,
      kind: ChangeApprovalKind.RecipientDestination,
      resourceName: 'Ops webhook',
      changeSummary: 'Destination changed',
      proposedMigrationJob: null,
      previousRecipientDestination: {
        notificationType: NotificationType.Webhook,
        destination: 'https://hooks.example.com/ops/********',
        headerNames: ['X-Api-Key', 'X-Team'],
        headers: [
          { name: 'X-Api-Key', fingerprint: '0a1b2c3d4e5f' },
          { name: 'X-Team', fingerprint: '111111111111' },
        ],
        fingerprint: 'aaaaaaaaaaaa',
        unreadable: false,
      },
      proposedRecipientDestination: {
        notificationType: NotificationType.Webhook,
        destination: 'https://hooks.example.com/risk/********',
        headerNames: ['X-Api-Key', 'X-Region'],
        headers: [
          { name: 'X-Api-Key', fingerprint: 'ffffeeeedddd' },
          { name: 'X-Region', fingerprint: '222222222222' },
        ],
        fingerprint: 'bbbbbbbbbbbb',
        unreadable: false,
      },
    });

    renderDetail();

    const destination = await screen.findByRole('region', { name: 'Recipient destination' });
    expect(within(destination).getByText('https://hooks.example.com/ops/********')).toBeInTheDocument();
    expect(within(destination).getByText('https://hooks.example.com/risk/********')).toBeInTheDocument();
    expect(within(destination).getByText('aaaaaaaaaaaa')).toBeInTheDocument();
    expect(within(destination).getByText('bbbbbbbbbbbb')).toBeInTheDocument();
    const headers = within(destination).getByRole('list', { name: 'Custom headers' });
    const apiKey = within(headers).getByText('X-Api-Key').closest('li') as HTMLElement;
    expect(within(apiKey).getByText('ffffeeeedddd')).toBeInTheDocument();
    expect(within(apiKey).getByText('Value changed')).toBeInTheDocument();
    expect(within(within(headers).getByText('X-Region').closest('li') as HTMLElement).getByText('Added')).toBeInTheDocument();
    expect(within(within(headers).getByText('X-Team').closest('li') as HTMLElement).getByText('Removed')).toBeInTheDocument();
    expect(screen.queryByText('The change is in a part the mask hides')).not.toBeInTheDocument();
    expect(await screen.findByLabelText(/Reviewer comment \(optional\)/)).toBeInTheDocument();
  });

  it('warns when a recipient change hides behind the mask, and approves it only with a comment', async () => {
    const masked = {
      notificationType: NotificationType.Slack,
      destination: 'https://hooks.slack.com/services/T0001/B0002/********',
      headerNames: [],
      headers: [],
      unreadable: false,
    };
    serve({
      ...DETAIL,
      kind: ChangeApprovalKind.RecipientDestination,
      resourceName: 'Ops channel',
      proposedMigrationJob: null,
      previousRecipientDestination: { ...masked, fingerprint: '0123456789ab' },
      proposedRecipientDestination: { ...masked, fingerprint: 'ba9876543210' },
      changeHiddenByMask: true,
      commentRequiredToApprove: true,
    });
    let body: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/approvals/changes/31/approve', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderDetail();

    expect(await screen.findByText('The change is in a part the mask hides')).toBeInTheDocument();
    expect(screen.getByText(/approving it requires a comment/)).toBeInTheDocument();
    const approve = await screen.findByRole('button', { name: 'Approve' });
    expect(approve).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeEnabled();

    fireEvent.change(screen.getByLabelText(/Reviewer comment \(required to approve\)/), {
      target: { value: 'Compared the fingerprint with the requester.' },
    });
    expect(approve).toBeEnabled();
    fireEvent.click(approve);

    await waitFor(() =>
      expect(body).toEqual({ comment: 'Compared the fingerprint with the requester.', expectedPayloadHash: 'hash-of-what-is-shown' }),
    );
  });

  it('says when the proposed destination cannot be read', async () => {
    serve({
      ...DETAIL,
      kind: ChangeApprovalKind.RecipientDestination,
      proposedMigrationJob: null,
      proposedRecipientDestination: { notificationType: NotificationType.Webhook, destination: '', headerNames: [], unreadable: true },
      mayApprove: false,
      approveBlockedReason: 'The proposed destination cannot be read, so it cannot be approved.',
    });

    renderDetail();

    expect(await screen.findByText('The proposed destination cannot be read.')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Approve' })).toBeDisabled();
  });

  it('shows someone who is not an Approver the change without a decision', async () => {
    serve(DETAIL, ['MigrationOperator']);

    renderDetail();

    expect(await screen.findByRole('region', { name: 'Migration job definition' })).toBeInTheDocument();
    expect(screen.getByText('An Approver decides this change')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
  });
});

describe('the approvals list by kind of change', () => {
  it('lists the pending changes of the kind an Approver picks, and opens one', async () => {
    let kindAsked: string | null = null;
    mswServer.use(
      permissionsHandler({ permissions: ['Approver'] }),
      http.get('*/beacon/api/approvals/pending', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
      http.get('*/beacon/api/approvals/changes', ({ request }) => {
        kindAsked = new URL(request.url).searchParams.get('kind');
        return HttpResponse.json({
          items: [
            {
              id: 31,
              kind: ChangeApprovalKind.RecipientDestination,
              resourceId: 4,
              resourceName: 'Ops channel',
              status: ApprovalStatus.Pending,
              requestedByUserName: 'Sam Security',
              createdTime: '2026-10-13T10:00:00Z',
              changeSummary: 'Destination changed',
            },
          ],
          totalCount: 1,
          pageCount: 1,
        });
      }),
    );

    renderWithProviders(
      <Routes>
        <Route path="/approvals" element={<ApprovalsListPage />} />
        <Route path="/approvals/changes/:id" element={<div>Change 31</div>} />
      </Routes>,
      { initialEntries: ['/approvals'] },
    );
    fireEvent.click(await screen.findByRole('radio', { name: 'Recipients' }));

    expect(await screen.findByText('Ops channel')).toBeInTheDocument();
    expect(kindAsked).toBe(`${ChangeApprovalKind.RecipientDestination}`);
    expect(screen.getByText('Destination changed')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Ops channel'));
    expect(await screen.findByText('Change 31')).toBeInTheDocument();
  });

  it('offers anyone else the query, migration job and Custom SQL requests they may read, never the recipient ones', async () => {
    let kindAsked: string | null = null;
    mswServer.use(
      permissionsHandler({ permissions: [] }),
      http.get('*/beacon/api/approvals/pending', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
      http.get('*/beacon/api/approvals/changes', ({ request }) => {
        kindAsked = new URL(request.url).searchParams.get('kind');
        return HttpResponse.json({
          items: [
            {
              id: 31,
              kind: ChangeApprovalKind.MigrationJob,
              resourceId: 4,
              resourceName: 'Nightly copy',
              status: ApprovalStatus.Pending,
              requestedByUserName: 'Ola Operator',
              createdTime: '2026-10-13T10:00:00Z',
              changeSummary: 'Migration job created',
            },
          ],
          totalCount: 1,
          pageCount: 1,
        });
      }),
    );

    // A link to the recipient requests falls back to the query requests.
    renderWithProviders(<ApprovalsListPage />, { initialEntries: ['/approvals?kind=3'] });

    const kinds = await screen.findByRole('radiogroup', { name: 'Kind of change' });
    expect(within(kinds).getByRole('radio', { name: 'Queries' })).toHaveAttribute('aria-checked', 'true');
    expect(within(kinds).queryByRole('radio', { name: 'Recipients' })).not.toBeInTheDocument();
    expect(await screen.findByText('No pending approvals')).toBeInTheDocument();

    fireEvent.click(within(kinds).getByRole('radio', { name: 'Migration jobs' }));

    expect(await screen.findByText('Nightly copy')).toBeInTheDocument();
    expect(kindAsked).toBe(`${ChangeApprovalKind.MigrationJob}`);
  });
});

describe('reviewing a change of a data source connection or the LLM connection', () => {
  const CONNECTION = {
    name: 'ledger-db',
    dataSourceType: DataSourceType.Database,
    databaseEngineType: DatabaseEngineType.PostgreSQL,
    host: 'ledger.db.internal',
    port: 5432,
    database: 'ledger',
    login: 'beacon_reader',
    changedSettings: [],
    secretOnlyDefinition: false,
    fingerprint: '111111111111',
    authenticatesServer: true,
    databaseReadOnlyEnforced: true,
    isTargetChange: false,
  };
  const CONNECTION_DETAIL = {
    ...DETAIL,
    kind: ChangeApprovalKind.DataSourceConnection,
    resourceId: 9,
    resourceName: 'ledger-db',
    changeSummary: 'Host, Database changed',
    proposedMigrationJob: null,
    previousDataSourceConnection: CONNECTION,
    proposedDataSourceConnection: {
      ...CONNECTION,
      host: 'ledger-replica.db.internal',
      database: 'ledger_reporting',
      changedSettings: ['Database', 'Host', 'Options'],
      fingerprint: '222222222222',
      isTargetChange: true,
      // Never sent by the server; a field the page does not know is not shown.
      password: 'not-shown-9f8e',
    },
    previousLlmConnection: null,
    proposedLlmConnection: null,
    currentProjects: [{ id: 5, name: 'Ledger' }],
  };

  it('shows the host, port, database and login, the names of the changed settings, the fingerprints and the linked projects', async () => {
    serve(CONNECTION_DETAIL);

    renderDetail();

    const connection = await screen.findByRole('region', { name: 'Data source connection' });
    expect(within(connection).getByText('ledger.db.internal')).toBeInTheDocument();
    expect(within(connection).getByText('ledger-replica.db.internal')).toBeInTheDocument();
    expect(within(connection).getByText('ledger_reporting')).toBeInTheDocument();
    expect(within(connection).getByText('beacon_reader')).toBeInTheDocument();
    expect(within(connection).getByText('5432')).toBeInTheDocument();
    expect(within(connection).getByText('111111111111')).toBeInTheDocument();
    expect(within(connection).getByText('222222222222')).toBeInTheDocument();
    const settings = within(connection).getByRole('list', { name: 'Changed settings' });
    expect(within(settings).getAllByRole('listitem').map(x => x.textContent)).toEqual(['Database', 'Host', 'Options']);
    expect(within(within(connection).getByRole('list', { name: 'Linked projects' })).getByText('Ledger')).toBeInTheDocument();
    expect(within(connection).getByText('Points the source at another server or database')).toBeInTheDocument();
    expect(screen.queryByText(/not-shown-9f8e/)).not.toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'ledger-db' })).toHaveAttribute('href', '/data-sources/9');
  });

  it('says a connection its connector cannot show is compared by fingerprint, and approves it only with a comment', async () => {
    serve({
      ...CONNECTION_DETAIL,
      previousDataSourceConnection: null,
      proposedDataSourceConnection: {
        ...CONNECTION,
        dataSourceType: DataSourceType.BigQuery,
        databaseEngineType: null,
        host: null,
        port: null,
        database: null,
        login: null,
        secretOnlyDefinition: true,
        fingerprint: '333333333333',
      },
      commentRequiredToApprove: true,
    });

    renderDetail();

    const connection = await screen.findByRole('region', { name: 'Data source connection' });
    expect(within(connection).getByText(/The data source is new/)).toBeInTheDocument();
    expect(within(connection).getByText(/compared only by its fingerprint/)).toBeInTheDocument();
    expect(within(connection).getByText('333333333333')).toBeInTheDocument();
    expect(within(connection).queryByRole('list', { name: 'Changed settings' })).not.toBeInTheDocument();
    const approve = await screen.findByRole('button', { name: 'Approve' });
    expect(approve).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Reviewer comment \(required to approve\)/), {
      target: { value: 'Compared the fingerprint with the requester.' },
    });
    expect(approve).toBeEnabled();
  });

  it("shows the LLM connection's provider, endpoint host, models and fingerprints, never a credential", async () => {
    const llm = {
      fromHostConfiguration: false,
      provider: AiProvider.OpenAI,
      endpoint: null,
      endpointFingerprint: null,
      region: null,
      bedrockAuthMode: BedrockAuthMode.IamRole,
      model: 'gpt-4o',
      fastModel: 'gpt-4o-mini',
      basedOn: null,
      credentials: [{ name: 'API key', fingerprint: 'aaaaaaaaaaaa' }],
    };
    serve(
      {
        ...DETAIL,
        kind: ChangeApprovalKind.LlmConnection,
        resourceId: 0,
        resourceName: 'LLM connection',
        changeSummary: 'Provider, Endpoint changed',
        proposedMigrationJob: null,
        previousLlmConnection: llm,
        proposedLlmConnection: {
          ...llm,
          provider: AiProvider.AzureOpenAI,
          endpoint: 'https://contoso.openai.azure.com',
          endpointFingerprint: 'cccccccccccc',
          basedOn: 'dddddddddddd',
          credentials: [{ name: 'API key', fingerprint: 'bbbbbbbbbbbb' }],
          // Never sent by the server; a field the page does not know is not shown.
          apiKey: 'sk-not-shown-1234',
        },
        currentProjects: [],
      },
      [],
    );
    mswServer.use(permissionsHandler({ isAdmin: true }));

    renderDetail();

    const connection = await screen.findByRole('region', { name: 'LLM connection' });
    expect(within(connection).getByText('OpenAI')).toBeInTheDocument();
    expect(within(connection).getByText('Azure OpenAI')).toBeInTheDocument();
    expect(within(connection).getByText('https://contoso.openai.azure.com')).toBeInTheDocument();
    expect(within(connection).getByText('cccccccccccc')).toBeInTheDocument();
    expect(within(connection).getByText('dddddddddddd')).toBeInTheDocument();
    const apiKey = within(within(connection).getByRole('list', { name: 'Credentials' })).getByText('API key').closest('li') as HTMLElement;
    expect(within(apiKey).getByText('bbbbbbbbbbbb')).toBeInTheDocument();
    expect(within(apiKey).getByText('Value changed')).toBeInTheDocument();
    expect(screen.queryByText(/sk-not-shown-1234/)).not.toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'LLM connection' })).toHaveAttribute('href', '/admin-settings');
  });

  it('says when the LLM connection was the host configuration, without values', async () => {
    serve(
      {
        ...DETAIL,
        kind: ChangeApprovalKind.LlmConnection,
        resourceId: 0,
        resourceName: 'LLM connection',
        proposedMigrationJob: null,
        previousLlmConnection: { fromHostConfiguration: true, provider: null, endpoint: null, endpointFingerprint: null, region: null, bedrockAuthMode: BedrockAuthMode.IamRole, model: null, fastModel: null, basedOn: null, credentials: [] },
        proposedLlmConnection: { fromHostConfiguration: false, provider: AiProvider.Claude, endpoint: null, endpointFingerprint: null, region: null, bedrockAuthMode: BedrockAuthMode.IamRole, model: 'claude-model', fastModel: null, basedOn: 'eeeeeeeeeeee', credentials: [] },
        currentProjects: [],
      },
      [],
    );
    mswServer.use(permissionsHandler({ isAdmin: true }));

    renderDetail();

    const connection = await screen.findByRole('region', { name: 'LLM connection' });
    expect(within(connection).getByText(/the host's configuration is in use/)).toBeInTheDocument();
    expect(within(connection).getByText('Anthropic')).toBeInTheDocument();
    expect(within(connection).getByText('No credential is stored.')).toBeInTheDocument();
  });

  it('shows an Admin role grant by the user label, the role and the requester, with no user name or e-mail', async () => {
    serve(
      {
        ...DETAIL,
        kind: ChangeApprovalKind.AdminRoleGrant,
        resourceId: 9,
        resourceName: 'User 9',
        changeSummary: 'Admin role granted',
        proposedMigrationJob: null,
        proposedAdminRoleGrant: { userId: 9, roleId: 1, roleName: 'Admin', userName: 'secret.name', email: 'secret@example.test' },
      },
      [],
    );
    mswServer.use(permissionsHandler({ isAdmin: true }));

    renderDetail();

    const grant = await screen.findByRole('region', { name: 'Admin role grant' });
    expect(within(grant).getByText('User 9')).toBeInTheDocument();
    expect(within(grant).getByText('Admin')).toBeInTheDocument();
    expect(within(grant).getByText('Ola Operator')).toBeInTheDocument();
    expect(screen.queryByText(/secret/)).not.toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'User 9' })).toHaveAttribute('href', '/users');
    expect(await screen.findByRole('button', { name: /approve/i })).toBeEnabled();
  });
});
