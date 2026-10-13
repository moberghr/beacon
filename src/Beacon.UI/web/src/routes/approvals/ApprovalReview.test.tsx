import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { ApprovalStatus } from '@/lib/enums';
import ApprovalDetailPage from './ApprovalDetailPage';
import { ReviewApprovalDialog } from './ReviewApprovalDialog';

const step = (order: number, sql: string, source = 'finance-db', parameters: unknown[] = []) => ({
  stepOrder: order,
  sqlValue: sql,
  dataSourceId: source === 'finance-db' ? 4 : 5,
  dataSourceName: source,
  name: `Step ${order}`,
  description: null,
  parameters,
});

const version = (id: number, versionNumber: number, steps: unknown[], finalQuery: string | null) => ({
  id,
  versionNumber,
  label: null,
  status: 3,
  name: 'Open invoices',
  description: null,
  finalQuery,
  createdTime: '2026-06-01T10:00:00Z',
  createdByUserName: 'Ana Analyst',
  changeSource: 'UserEdit',
  changeReason: null,
  steps,
});

const ACTIVE_STEPS = [
  step(1, 'SELECT id FROM invoices', 'finance-db', [{ name: 'n', type: 1, description: null, placeholder: '{n}' }]),
  step(2, 'SELECT id FROM refunds'),
];

const PROPOSED_STEPS = [
  step(1, 'SELECT id FROM invoices WHERE open', 'ledger-db', [{ name: 'n', type: 3, description: null, placeholder: '{n}' }]),
  step(2, 'SELECT 1'),
];

// A pending change of a live query: step 1 rewritten on another source with a retyped parameter, step 2 replaced by a
// new step 2, the old step 2 removed at position 3, and the final query changed.
const DETAIL = {
  id: 7,
  queryId: 3,
  queryName: 'Open invoices',
  queryVersionId: 11,
  status: ApprovalStatus.Pending,
  requestedByUserId: 'u1',
  requestedByUserName: 'Ana',
  reviewedByUserName: null,
  reviewedAt: null,
  reviewComment: null,
  changeSummary: 'SQL query modified',
  createdTime: '2026-06-01T10:00:00Z',
  proposedVersion: version(11, 2, PROPOSED_STEPS, 'SELECT * FROM step1'),
  currentActiveVersion: { ...version(10, 1, ACTIVE_STEPS, 'SELECT * FROM step2'), status: 1 },
  autoDiff: {
    versionA: null,
    versionB: null,
    nameChanged: false,
    descriptionChanged: false,
    finalQueryChanged: true,
    stepDiffs: [
      { stepOrder: 1, diffType: 1, stepA: ACTIVE_STEPS[0], stepB: PROPOSED_STEPS[0] },
      { stepOrder: 2, diffType: 2, stepA: null, stepB: PROPOSED_STEPS[1] },
      { stepOrder: 3, diffType: 3, stepA: ACTIVE_STEPS[1], stepB: null },
    ],
  },
  contentHash: 'hash-of-what-is-shown',
  isOwnRequest: false,
  mayApprove: true,
  approveBlockedReason: null,
  isStale: false,
};

function serve(detail: Record<string, unknown>) {
  mswServer.use(
    permissionsHandler({ permissions: ['Approver'] }),
    http.get('*/beacon/api/approvals/7', () => HttpResponse.json(detail)),
  );
}

function renderPage() {
  renderWithProviders(
    <Routes>
      <Route path="/approvals/:id" element={<ApprovalDetailPage />} />
      <Route path="/approvals" element={<div>Approvals list</div>} />
    </Routes>,
    { initialEntries: ['/approvals/7'] },
  );
}

describe('reviewing a query change', () => {
  it("shows each step's SQL, data source and parameters as the server pairs them, and the final query", async () => {
    serve(DETAIL);

    renderPage();

    const first = await screen.findByRole('region', { name: 'Step 1' });
    expect(within(first).getByText('Modified')).toBeInTheDocument();
    expect(within(first).getByText('SELECT id FROM invoices')).toBeInTheDocument();
    expect(within(first).getByText('SELECT id FROM invoices WHERE open')).toBeInTheDocument();
    expect(within(first).getByText('finance-db')).toBeInTheDocument();
    expect(within(first).getByText('ledger-db')).toBeInTheDocument();
    expect(within(first).getByText('n · Number · {n}')).toBeInTheDocument();
    expect(within(first).getByText('n · String · {n}')).toBeInTheDocument();

    const second = screen.getByRole('region', { name: 'Step 2' });
    expect(within(second).getByText('Added')).toBeInTheDocument();
    expect(within(second).getByText('SELECT 1')).toBeInTheDocument();

    const third = screen.getByRole('region', { name: 'Step 3' });
    expect(within(third).getByText('Removed')).toBeInTheDocument();
    expect(within(third).getByText('SELECT id FROM refunds')).toBeInTheDocument();

    const final = screen.getByRole('region', { name: 'Final query' });
    expect(within(final).getByText('SELECT * FROM step2')).toBeInTheDocument();
    expect(within(final).getByText('SELECT * FROM step1')).toBeInTheDocument();
  });

  it('shows every proposed step as added for a new query that has no active version', async () => {
    serve({ ...DETAIL, currentActiveVersion: null, autoDiff: null });

    renderPage();

    expect(await screen.findByText(/has no active version yet/)).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Step 1' })).getByText('Added')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Step 2' })).getByText('Added')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Final query' })).getByText('Added')).toBeInTheDocument();
  });

  it('approves the content it showed: the content hash of the detail goes with the approval', async () => {
    serve(DETAIL);
    let body: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/approvals/7/approve', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(body).toEqual({ comment: null, expectedContentHash: 'hash-of-what-is-shown' }));
  });

  it('does not offer the requester their own approval, and says why; they may still withdraw it', async () => {
    serve({
      ...DETAIL,
      isOwnRequest: true,
      mayApprove: false,
      approveBlockedReason: 'A change is approved by someone other than its requester.',
    });

    renderPage();

    expect(await screen.findByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeEnabled();
    expect(screen.getByText('You cannot approve this request')).toBeInTheDocument();
    expect(screen.getByText('A change is approved by someone other than its requester.')).toBeInTheDocument();
  });

  it('says a request is stale and does not offer its approval', async () => {
    serve({
      ...DETAIL,
      mayApprove: false,
      isStale: true,
      approveBlockedReason: 'The query changed after this request was made.',
    });

    renderPage();

    expect(await screen.findByText('The query changed since this request was made')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.queryByText('You cannot approve this request')).not.toBeInTheDocument();
  });

  it('shows a superseded request without a decision', async () => {
    serve({ ...DETAIL, status: ApprovalStatus.Superseded, mayApprove: false });

    renderPage();

    expect(await screen.findByText('A newer submission replaced this request')).toBeInTheDocument();
    expect(screen.getByText('Superseded')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
  });

  it('applies the same rules in the review dialog', async () => {
    serve({ ...DETAIL, isOwnRequest: true, mayApprove: false, approveBlockedReason: 'A change is approved by someone other than its requester.' });

    renderWithProviders(<ReviewApprovalDialog open approvalId={7} onClose={() => undefined} />);

    expect(await screen.findByRole('region', { name: 'Step 1' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^approve$/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^reject$/i })).toBeEnabled();
    expect(screen.getByText('A change is approved by someone other than its requester.')).toBeInTheDocument();
  });

  it('offers no decision in the review dialog once the request was superseded', async () => {
    serve({ ...DETAIL, status: ApprovalStatus.Superseded, mayApprove: false });

    renderWithProviders(<ReviewApprovalDialog open approvalId={7} onClose={() => undefined} />);

    expect(await screen.findByText('A newer submission replaced this request')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^approve$/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /^reject$/i })).toBeDisabled();
  });
});
