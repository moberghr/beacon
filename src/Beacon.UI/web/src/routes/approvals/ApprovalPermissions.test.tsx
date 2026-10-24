import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { ApprovalStatus } from '@/lib/enums';
import ApprovalsListPage from './ApprovalsListPage';
import ApprovalDetailPage from './ApprovalDetailPage';

const SUMMARY = {
  id: 7,
  queryId: 3,
  queryName: 'Open invoices',
  versionNumber: 2,
  status: ApprovalStatus.Pending,
  requestedByUserName: 'Ana',
  createdTime: '2026-06-01T10:00:00Z',
  changeSummary: 'Tightened the WHERE clause',
};

const DETAIL = {
  ...SUMMARY,
  queryVersionId: 11,
  requestedByUserId: 'u1',
  reviewedByUserName: null,
  reviewedAt: null,
  reviewComment: null,
  proposedVersion: {
    id: 11,
    versionNumber: 2,
    label: null,
    status: 3,
    name: 'Open invoices',
    description: null,
    finalQuery: 'SELECT * FROM invoices WHERE open = true',
    createdTime: '2026-06-01T10:00:00Z',
    createdByUserName: 'Ana Analyst',
    changeSource: null,
    changeReason: null,
    steps: [
      {
        stepOrder: 1,
        sqlValue: 'SELECT * FROM invoices WHERE open = true',
        dataSourceId: 4,
        dataSourceName: 'finance-db',
        name: 'Open',
        description: null,
        parameters: [],
      },
    ],
  },
  currentActiveVersion: null,
  autoDiff: null,
  contentHash: 'hash-11',
  isOwnRequest: false,
  mayApprove: true,
  approveBlockedReason: null,
  isStale: false,
};

function serveApprovals() {
  mswServer.use(
    http.get('*/beacon/api/approvals/pending', () => HttpResponse.json({ items: [SUMMARY], totalCount: 1, pageCount: 1 })),
    http.get('*/beacon/api/approvals/7', () => HttpResponse.json(DETAIL)),
  );
}

function renderDetail() {
  renderWithProviders(
    <Routes>
      <Route path="/approvals/:id" element={<ApprovalDetailPage />} />
    </Routes>,
    { initialEntries: ['/approvals/7'] },
  );
}

describe('approvals and the Approver permission', () => {
  it('offers an Approver the review on the list', async () => {
    serveApprovals();
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(<ApprovalsListPage />);

    const row = (await screen.findByText('Open invoices')).closest('[role="row"]') as HTMLElement;
    expect(await within(row).findByRole('button', { name: 'Review' })).toBeInTheDocument();
  });

  it('lists the requests read-only for a user who is not an Approver', async () => {
    serveApprovals();

    renderWithProviders(<ApprovalsListPage />);

    expect(await screen.findByText('Open invoices')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Review' })).not.toBeInTheDocument());
  });

  it("shows an Approver with no Viewer role the decision, and the query's name without a link", async () => {
    serveApprovals();
    mswServer.use(functionalOnly('Approver'));

    renderDetail();

    expect(await screen.findByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Open invoices' })).not.toBeInTheDocument();
  });

  it('shows no decision to a user who is not an Approver', async () => {
    serveApprovals();

    renderDetail();

    expect(await screen.findByText('Proposed change')).toBeInTheDocument();
    expect(screen.getByText('Proposed SQL')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Open invoices' })).toHaveAttribute('href', '/queries/3');
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });
});

describe('the kinds of change each caller may read', () => {
  const noRequests = http.get('*/beacon/api/approvals/pending', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 }));

  // The permissions load after the first render: each test waits (findByRole) for an option they decide.
  function kindsShown() {
    return screen.findByRole('radiogroup', { name: 'Kind of change' });
  }

  it('offers an Approver the data-source connection changes, not the LLM connection ones', async () => {
    mswServer.use(noRequests, permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(<ApprovalsListPage />);

    const kinds = await kindsShown();
    expect(await within(kinds).findByRole('radio', { name: 'Data sources' })).toBeInTheDocument();
    expect(within(kinds).queryByRole('radio', { name: 'LLM connection' })).not.toBeInTheDocument();
  });

  it('offers an Admin the LLM connection changes too', async () => {
    mswServer.use(noRequests, permissionsHandler({ isAdmin: true }));

    renderWithProviders(<ApprovalsListPage />);

    const kinds = await kindsShown();
    expect(await within(kinds).findByRole('radio', { name: 'LLM connection' })).toBeInTheDocument();
    expect(within(kinds).getByRole('radio', { name: 'Data sources' })).toBeInTheDocument();
  });

  it('offers someone who is not an Approver neither, and a link to them falls back to the query requests', async () => {
    mswServer.use(noRequests, permissionsHandler({ permissions: ['DataSourceAdmin', 'SecurityAdmin'] }));

    renderWithProviders(<ApprovalsListPage />, { initialEntries: ['/approvals?kind=5'] });

    const kinds = await kindsShown();
    expect(await within(kinds).findByRole('radio', { name: 'Migration jobs' })).toBeInTheDocument();
    expect(within(kinds).queryByRole('radio', { name: 'Data sources' })).not.toBeInTheDocument();
    expect(within(kinds).queryByRole('radio', { name: 'LLM connection' })).not.toBeInTheDocument();
    expect(within(kinds).getByRole('radio', { name: 'Queries' })).toHaveAttribute('aria-checked', 'true');
  });

  it('sends an Approver who is not an Admin opening the LLM kind back to the query requests', async () => {
    mswServer.use(noRequests, permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(<ApprovalsListPage />, { initialEntries: ['/approvals?kind=5'] });

    const kinds = await kindsShown();
    expect(await within(kinds).findByRole('radio', { name: 'Data sources' })).toBeInTheDocument();
    expect(within(kinds).queryByRole('radio', { name: 'LLM connection' })).not.toBeInTheDocument();
    expect(within(kinds).getByRole('radio', { name: 'Queries' })).toHaveAttribute('aria-checked', 'true');
  });
});
