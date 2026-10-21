import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { GroundingRecheckKind, McpDocPatchStatus, McpDocPatchTarget, McpPatternStatus, McpPatternType } from '@/lib/enums';
import McpLearningPage from './McpLearningPage';
import { CHANGED_SINCE_REVIEWED } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/**
 * Curating grounding from the learning page: a pattern's or patch's actions are offered only when the server says the
 * caller may curate it (otherwise the reason is shown), the pattern status picker offers only Approve, Reject and back
 * to Pending, the detail views show the full content and who reviewed, applied or rejected it, and an Admin — only an
 * Admin — sees the golden candidates (promoted idempotently: an answer promoted already shows its existing case) and a
 * project's golden examples with who promoted and who last edited them.
 */

const STATS = {
  totalSignals: 10,
  signals7d: 4,
  signals30d: 10,
  successRate: 0.9,
  patternsApproved: 1,
  patternsPending: 1,
  patternsRejected: 0,
  patchesApplied: 1,
  patchesProposed: 2,
  problemTables: [],
};

function pattern(overrides: Record<string, unknown>) {
  return {
    id: 1,
    projectId: 1,
    dataSourceId: 1,
    schemaName: 'sales',
    tableName: 'orders',
    columnName: 'status',
    patternType: McpPatternType.ColumnClarification,
    patternContent: 'The status column holds shipped, pending and cancelled — and nothing else, ever.',
    exampleQuestion: 'How many shipped orders?',
    exampleSql: "SELECT count(*) FROM sales.orders WHERE status = 'shipped'",
    signalCount: 6,
    confidence: 0.9,
    status: McpPatternStatus.Pending,
    createdTime: '2026-10-01T08:00:00Z',
    lastRefreshedAt: null,
    reviewedByName: null,
    reviewedAt: null,
    canCurate: true,
    contentHash: 'a1b2c3',
    ...overrides,
  };
}

function patch(overrides: Record<string, unknown>) {
  return {
    id: 1,
    projectId: 1,
    dataSourceId: 1,
    targetType: McpDocPatchTarget.ColumnDescription,
    targetIdentifier: 'sales.orders.status',
    currentContent: 'Order status.',
    proposedContent: 'Order status: shipped, pending or cancelled; cancelled orders keep their lines.',
    reasoning: 'Asked about often.',
    supportingSignalCount: 4,
    status: McpDocPatchStatus.Proposed,
    createdTime: '2026-10-01T08:00:00Z',
    appliedAt: null,
    appliedByName: null,
    canCurate: true,
    ...overrides,
  };
}

const paged = (items: unknown[]) => ({ items, totalCount: items.length, pageCount: items.length ? 1 : 0 });

function mockLearning(patterns: unknown[], patches: unknown[] = []) {
  mswServer.use(
    http.get('*/beacon/api/mcp/learning-stats', () => HttpResponse.json(STATS)),
    http.get('*/beacon/api/mcp/learned-patterns', () => HttpResponse.json(paged(patterns))),
    http.get('*/beacon/api/mcp/documentation-patches', () => HttpResponse.json(paged(patches))),
  );
}

function asAdmin() {
  mswServer.use(permissionsHandler({ isAdmin: true }));
}

const CANDIDATE = {
  signalId: 41,
  projectId: 1,
  dataSourceId: 1,
  question: 'How many orders shipped last week?',
  sql: 'SELECT count(*) FROM sales.orders',
  isUserCorrected: true,
  feedbackNote: 'matches the finance report',
  askedByUserId: 5,
  askedByName: 'Ada Lovelace',
  ratedByUserId: 6,
  ratedByName: 'User 6',
  createdTime: '2026-10-02T08:00:00Z',
  sqlHash: 'd4e5f6',
};

const GOLDEN_CASE = {
  id: 7,
  projectId: 1,
  dataSourceId: 1,
  question: 'How many orders shipped last week?',
  goldSql: 'SELECT count(*) FROM sales.orders',
  sourceSignalId: 41,
  isActive: true,
  notes: null,
  createdTime: '2026-10-03T08:00:00Z',
  promotedByUserId: 9,
  promotedByName: 'Grace Hopper',
  modifiedByUserId: 10,
  modifiedByName: 'User 10',
};

function rowOf(text: string | RegExp): HTMLElement {
  return screen.getByText(text).closest('tr') as HTMLElement;
}

async function findRow(text: string | RegExp): Promise<HTMLElement> {
  return (await screen.findByText(text)).closest('tr') as HTMLElement;
}

const problem = (status: number, detail: string) =>
  HttpResponse.json({ title: 'Refused', status, detail }, { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('McpLearningPage curation', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('offers a curator only Approve, Reject and back to Pending, never the status a pattern already has', async () => {
    mockLearning([
      pattern({ id: 1, tableName: 'orders', status: McpPatternStatus.Pending }),
      pattern({ id: 2, tableName: 'invoices', status: McpPatternStatus.AutoApproved }),
      pattern({ id: 3, tableName: 'refunds', status: McpPatternStatus.Approved }),
    ]);

    renderWithProviders(<McpLearningPage />);
    await screen.findByText('orders');

    const pending = within(rowOf('orders'));
    expect(pending.getByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(pending.getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    expect(pending.queryByRole('button', { name: 'Back to pending' })).toBeNull();
    const autoApproved = within(rowOf('invoices'));
    expect(autoApproved.getAllByRole('button').map(x => x.textContent)).toEqual(['View', 'Approve', 'Reject', 'Back to pending']);
    const approved = within(rowOf('refunds'));
    expect(approved.getAllByRole('button').map(x => x.textContent)).toEqual(['View', 'Reject', 'Back to pending']);
  });

  it('sends the new status and the content hash the reviewer saw when a pattern is reviewed', async () => {
    mockLearning([pattern({ id: 1, status: McpPatternStatus.Approved })]);
    let body: unknown = null;
    mswServer.use(
      http.put('*/beacon/api/mcp/learned-patterns/:id/status', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Back to pending' }));

    await waitFor(() => expect(body).toEqual({ newStatus: McpPatternStatus.Pending, reviewedContentHash: 'a1b2c3' }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Pattern set back to pending.'));
  });

  it('shows a caller who may not curate a pattern why, and no review action', async () => {
    mockLearning([pattern({ id: 1, canCurate: false })]);

    renderWithProviders(<McpLearningPage />);
    await screen.findByText('orders');

    const row = within(rowOf('orders'));
    expect(row.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(row.queryByRole('button', { name: 'Reject' })).toBeNull();
    expect(row.getByText('Project admins review')).toHaveAttribute('title', expect.stringMatching(/project admin of its project/i));

    fireEvent.click(row.getByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    expect(within(dialog).getByText('You cannot review this pattern')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('shows a pattern in full with who reviewed it', async () => {
    mockLearning([
      pattern({
        id: 1,
        status: McpPatternStatus.Rejected,
        reviewedByName: 'Grace Hopper',
        reviewedAt: '2026-10-04T10:00:00Z',
      }),
    ]);

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'View' }));

    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    expect(within(dialog).getByText('The status column holds shipped, pending and cancelled — and nothing else, ever.')).toBeInTheDocument();
    expect(within(dialog).getByText("SELECT count(*) FROM sales.orders WHERE status = 'shipped'")).toBeInTheDocument();
    expect(within(dialog).getByText(/Grace Hopper ·/)).toBeInTheDocument();
    expect(within(dialog).getAllByRole('button').map(x => x.textContent)).toEqual(['', 'Approve', 'Back to pending']);
  });

  it('offers apply and reject on a patch only to a curator, and says why to anyone else', async () => {
    mockLearning([], [
      patch({ id: 1, targetIdentifier: 'sales.orders.status', canCurate: true }),
      patch({ id: 2, targetIdentifier: 'sales.orders.total', canCurate: false }),
      patch({ id: 3, targetIdentifier: 'Overview', targetType: McpDocPatchTarget.DocumentationSection, canCurate: false }),
    ]);

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: /Doc patches/ }));
    await screen.findByText('sales.orders.status');

    expect(within(rowOf('sales.orders.status')).getByRole('button', { name: 'Apply' })).toBeInTheDocument();
    const shared = within(rowOf('sales.orders.total'));
    expect(shared.queryByRole('button', { name: 'Apply' })).toBeNull();
    expect(shared.getByText('Curators apply')).toHaveAttribute('title', expect.stringMatching(/every project linking its data source, a DataSourceAdmin/i));
    expect(within(rowOf('Overview')).getByText('Curators apply')).toHaveAttribute('title', expect.stringMatching(/project admin of its project/i));
  });

  it('shows a patch in full, current and proposed, with who applied it', async () => {
    mockLearning([], [
      patch({
        id: 1,
        status: McpDocPatchStatus.Applied,
        appliedByName: 'Grace Hopper',
        appliedAt: '2026-10-04T10:00:00Z',
      }),
    ]);

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: /Doc patches/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'View' }));

    const dialog = await screen.findByRole('dialog', { name: 'Documentation patch' });
    expect(within(dialog).getByText('Order status.')).toBeInTheDocument();
    expect(within(dialog).getByText('Order status: shipped, pending or cancelled; cancelled orders keep their lines.')).toBeInTheDocument();
    expect(within(dialog).getByText('Applied by')).toBeInTheDocument();
    expect(within(dialog).getByText(/Grace Hopper ·/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Apply' })).toBeNull();
  });

  it('shows the golden and recheck tabs to an Admin only', async () => {
    mockLearning([]);

    renderWithProviders(<McpLearningPage />);
    await screen.findByText('No patterns');

    expect(screen.queryByRole('tab', { name: 'Golden candidates' })).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Golden examples' })).toBeNull();
    expect(screen.queryByRole('tab', { name: 'Grounding recheck' })).toBeNull();
  });

  it('says a pattern changed since it was reviewed, reads the list again and closes its detail view', async () => {
    let reads = 0;
    mswServer.use(
      http.get('*/beacon/api/mcp/learning-stats', () => HttpResponse.json(STATS)),
      http.get('*/beacon/api/mcp/learned-patterns', () => {
        reads++;
        return HttpResponse.json(paged([pattern({ id: 1 })]));
      }),
      http.get('*/beacon/api/mcp/documentation-patches', () => HttpResponse.json(paged([]))),
      http.put('*/beacon/api/mcp/learned-patterns/:id/status', () =>
        problem(409, 'The pattern changed since it was reviewed: reload the learned patterns and review it again.')),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(CHANGED_SINCE_REVIEWED));
    expect(toast.error).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(reads).toBeGreaterThan(1));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Learned pattern' })).toBeNull());
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("keeps showing the server's reason for any other refused review", async () => {
    mockLearning([pattern({ id: 1 })]);
    mswServer.use(
      http.put('*/beacon/api/mcp/learned-patterns/:id/status', () =>
        problem(400, 'Pattern 1 was not approved: its example SQL does not pass the SQL check.')),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));

    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith('Pattern 1 was not approved: its example SQL does not pass the SQL check.'));
  });

  it('flags a pattern whose example prompts leave out, and shows hidden characters escaped', async () => {
    mockLearning([
      pattern({ id: 1, exampleQuestion: 'How many\u202E orders?', exampleSqlWithheld: true, exampleQuestionWithheld: true }),
      pattern({ id: 2, tableName: 'invoices' }),
    ]);

    renderWithProviders(<McpLearningPage />);
    await screen.findByText('orders');

    const flag = within(rowOf('orders')).getByText('Example left out of prompts');
    expect(flag.closest('[title]')).toHaveAttribute('title', expect.stringMatching(/example SQL holds a comment.*example question holds characters/));
    expect(within(rowOf('invoices')).queryByText('Example left out of prompts')).toBeNull();

    fireEvent.click(within(rowOf('orders')).getByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    expect(within(dialog).getByText('Prompts leave this example out (the lesson stays)')).toBeInTheDocument();
    expect(within(dialog).getByText('How many\\u{202E} orders?')).toBeInTheDocument();
    expect(within(dialog).getByText(/Hidden characters shown as/)).toBeInTheDocument();
  });

  it('names a reviewer recorded only by time as an unknown reviewer', async () => {
    mockLearning(
      [pattern({ id: 1, status: McpPatternStatus.Approved, reviewedByName: null, reviewedAt: '2026-10-04T10:00:00Z' })],
      [patch({ id: 1, status: McpDocPatchStatus.Applied, appliedByName: null, appliedAt: '2026-10-04T10:00:00Z' })],
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    expect(within(dialog).getByText(/^Unknown reviewer ·/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: /close/i }));

    fireEvent.click(screen.getByRole('tab', { name: /Doc patches/ }));
    expect(await screen.findByText('by Unknown reviewer')).toBeInTheDocument();
  });
});

describe('McpLearningPage golden curation (Admin)', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    asAdmin();
  });

  it('says a candidate changed since it was reviewed and reads the candidates again', async () => {
    mockLearning([]);
    let reads = 0;
    mswServer.use(
      http.get('*/beacon/api/eval/golden/candidates', () => {
        reads++;
        return HttpResponse.json(paged([CANDIDATE]));
      }),
      http.post('*/beacon/api/eval/golden/promote', () =>
        problem(409, 'The answer changed since it was reviewed (its rating or its SQL): reload the golden candidates and review it again.')),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden candidates' }));
    fireEvent.click(await screen.findByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Golden candidate' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Promote' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(CHANGED_SINCE_REVIEWED));
    await waitFor(() => expect(reads).toBeGreaterThan(1));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Golden candidate' })).toBeNull());
    expect(screen.queryByRole('dialog', { name: 'Golden case' })).toBeNull();
  });

  it('shows a candidate holding hidden characters escaped', async () => {
    mockLearning([]);
    mswServer.use(
      http.get('*/beacon/api/eval/golden/candidates', () =>
        HttpResponse.json(paged([{ ...CANDIDATE, question: 'How many\u200B orders?' }]))),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden candidates' }));

    expect(await screen.findByText('How many\\u{200B} orders?')).toBeInTheDocument();
  });

  it('lists the candidates and promotes one', async () => {
    mockLearning([]);
    let promoted: unknown = null;
    mswServer.use(
      http.get('*/beacon/api/eval/golden/candidates', () => HttpResponse.json(paged([CANDIDATE]))),
      http.post('*/beacon/api/eval/golden/promote', async ({ request }) => {
        promoted = await request.json();
        return HttpResponse.json({ evalCaseId: 7, alreadyPromoted: false });
      }),
      http.get('*/beacon/api/eval/golden', () => HttpResponse.json([GOLDEN_CASE])),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden candidates' }));

    const row = within((await screen.findByText(CANDIDATE.question)).closest('tr') as HTMLElement);
    expect(row.getByText('Ada Lovelace')).toBeInTheDocument();
    expect(row.getByText('User 6')).toBeInTheDocument();
    expect(row.getByText('Corrected')).toBeInTheDocument();
    fireEvent.click(row.getByRole('button', { name: 'Promote' }));

    await waitFor(() => expect(promoted).toEqual({ signalId: 41, reviewedSqlHash: 'd4e5f6', notes: null }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Promoted to golden case #7.'));
    const dialog = await screen.findByRole('dialog', { name: 'Golden case' });
    expect(await within(dialog).findByText('Grace Hopper')).toBeInTheDocument();
    expect(within(dialog).queryByText('This answer was already promoted')).toBeNull();
  });

  it('shows the existing case when the answer was promoted already', async () => {
    mockLearning([]);
    mswServer.use(
      http.get('*/beacon/api/eval/golden/candidates', () => HttpResponse.json(paged([CANDIDATE]))),
      http.post('*/beacon/api/eval/golden/promote', () => HttpResponse.json({ evalCaseId: 7, alreadyPromoted: true })),
      http.get('*/beacon/api/eval/golden', () => HttpResponse.json([GOLDEN_CASE])),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden candidates' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Promote' }));

    const dialog = await screen.findByRole('dialog', { name: 'Golden case' });
    expect(within(dialog).getByText('This answer was already promoted')).toBeInTheDocument();
    expect(within(dialog).getByText(/Nothing new was created: it is golden case #7/)).toBeInTheDocument();
    expect(await within(dialog).findByText('SELECT count(*) FROM sales.orders')).toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('shows an empty candidates list without inventing rows', async () => {
    mockLearning([]);
    mswServer.use(http.get('*/beacon/api/eval/golden/candidates', () => HttpResponse.json(paged([]))));

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden candidates' }));

    expect(await screen.findByText('No golden candidates')).toBeInTheDocument();
  });

  it("lists a project's golden examples with who promoted and who last edited them", async () => {
    mockLearning([]);
    let asked: string | null = null;
    mswServer.use(
      http.get('*/beacon/api/eval/golden', ({ request }) => {
        asked = new URL(request.url).searchParams.get('projectId');
        return HttpResponse.json([GOLDEN_CASE, { ...GOLDEN_CASE, id: 8, question: 'Older case', promotedByName: null, modifiedByName: null }]);
      }),
    );

    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden examples' }));
    expect(await screen.findByText('Choose a project')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Golden examples project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));

    const row = within((await screen.findByText(GOLDEN_CASE.question)).closest('tr') as HTMLElement);
    expect(asked).toBe('1');
    expect(row.getByText('Grace Hopper')).toBeInTheDocument();
    expect(row.getByText('User 10')).toBeInTheDocument();
    expect(within(rowOf('Older case')).getByText('Not recorded')).toBeInTheDocument();
  });
});

describe('McpLearningPage golden examples (Admin)', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    asAdmin();
  });

  async function openGoldenExamples() {
    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Golden examples' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Golden examples project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
  }

  it('flags the cases prompts leave out, with the reason, and shows hidden characters escaped', async () => {
    mockLearning([]);
    mswServer.use(
      http.get('*/beacon/api/eval/golden', () =>
        HttpResponse.json([
          { ...GOLDEN_CASE, question: 'Orders\u2066 shipped?', goldSqlWithheld: true, questionWithheld: true },
          { ...GOLDEN_CASE, id: 8, question: 'Kept case' },
        ])),
    );

    await openGoldenExamples();

    const row = within((await screen.findByText('Orders\\u{2066} shipped?')).closest('tr') as HTMLElement);
    expect(row.getByText('Left out of prompts').closest('[title]')).toHaveAttribute(
      'title',
      expect.stringMatching(/gold SQL holds a comment.*question holds characters/),
    );
    expect(within(rowOf('Kept case')).queryByText('Left out of prompts')).toBeNull();

    fireEvent.click(row.getByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Golden case' });
    expect(within(dialog).getByText('Prompts leave this case out')).toBeInTheDocument();
  });

  it('deactivates an active case after confirmation, and offers no Deactivate on an inactive one', async () => {
    mockLearning([]);
    let body: unknown = null;
    let reads = 0;
    mswServer.use(
      http.get('*/beacon/api/eval/golden', () => {
        reads++;
        return HttpResponse.json([GOLDEN_CASE, { ...GOLDEN_CASE, id: 8, question: 'Retired case', isActive: false }]);
      }),
      http.put('*/beacon/api/eval/golden/:id', async ({ request, params }) => {
        body = { id: params.id, ...(await request.json() as object) };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await openGoldenExamples();

    expect(within(await findRow('Retired case')).queryByRole('button', { name: 'Deactivate' })).toBeNull();
    fireEvent.click(within(rowOf(GOLDEN_CASE.question)).getByRole('button', { name: 'Deactivate' }));
    const confirm = await screen.findByRole('dialog', { name: 'Deactivate golden case' });
    expect(body).toBeNull();
    fireEvent.click(within(confirm).getByRole('button', { name: 'Deactivate' }));

    await waitFor(() => expect(body).toEqual({ id: '7', question: null, goldSql: null, isActive: false, notes: null }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Golden case #7 deactivated.'));
    await waitFor(() => expect(reads).toBeGreaterThan(1));
  });

  it('deactivates a case from its detail view', async () => {
    mockLearning([]);
    let deactivated = false;
    mswServer.use(
      http.get('*/beacon/api/eval/golden', () => HttpResponse.json([GOLDEN_CASE])),
      http.put('*/beacon/api/eval/golden/:id', () => {
        deactivated = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await openGoldenExamples();
    fireEvent.click(within(await findRow(GOLDEN_CASE.question)).getByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Golden case' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Deactivate golden case' })).getByRole('button', { name: 'Deactivate' }));

    await waitFor(() => expect(deactivated).toBe(true));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Golden case' })).toBeNull());
  });
});

describe('McpLearningPage grounding recheck (Admin)', () => {
  const REFUSED_CASE = {
    kind: GroundingRecheckKind.GoldenCase,
    id: 7,
    dataSourceId: 1,
    question: 'How many\u202E orders?',
    sql: 'SELECT count(*) FROM sales.orders',
    questionRefusal: 'The question holds characters that do not show on screen; a prompt leaves it out.',
    sqlRefusal: 'Beacon holds no tables or columns for this data source.',
    promotedByUserId: null,
    patternStatus: null,
    contentHash: null,
  };
  const REFUSED_PATTERN = {
    kind: GroundingRecheckKind.LearnedPattern,
    id: 21,
    dataSourceId: 1,
    question: 'orders last week',
    sql: 'SELECT missing_column FROM sales.orders',
    questionRefusal: null,
    sqlRefusal: 'Column missing_column is not in the catalog.',
    promotedByUserId: null,
    patternStatus: McpPatternStatus.AutoApproved,
    contentHash: 'f00d',
  };

  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    asAdmin();
  });

  async function runRecheck() {
    renderWithProviders(<McpLearningPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Grounding recheck' }));
    expect(await screen.findByText('Choose a project')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Recheck project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    expect(await screen.findByText('Not checked yet')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Run recheck' }));
  }

  it('reads nothing until asked, then lists what is refused with the reasons', async () => {
    mockLearning([]);
    const asked: (string | null)[] = [];
    mswServer.use(
      http.get('*/beacon/api/eval/grounding/recheck', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('projectId'));
        return HttpResponse.json({ projectId: 1, goldenCasesChecked: 3, patternsChecked: 2, refused: [REFUSED_CASE, REFUSED_PATTERN] });
      }),
    );

    await runRecheck();

    expect(await screen.findByText('Checked 3 active golden cases and 2 approved learned patterns.')).toBeInTheDocument();
    expect(asked).toEqual(['1']);
    const golden = within(rowOf('Golden case #7'));
    expect(golden.getByText('How many\\u{202E} orders?')).toBeInTheDocument();
    expect(golden.getByText(REFUSED_CASE.questionRefusal)).toBeInTheDocument();
    expect(golden.getByText(REFUSED_CASE.sqlRefusal)).toBeInTheDocument();
    expect(golden.getByText('Promoted before attribution')).toBeInTheDocument();
    expect(golden.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
    const learned = within(rowOf('Learned pattern #21'));
    expect(learned.getByText('Auto-approved')).toBeInTheDocument();
    expect(learned.getByText(REFUSED_PATTERN.sqlRefusal)).toBeInTheDocument();
    expect(learned.getByRole('button', { name: 'Back to pending' })).toBeInTheDocument();
  });

  it('says nothing is refused when every row passes', async () => {
    mockLearning([]);
    mswServer.use(
      http.get('*/beacon/api/eval/grounding/recheck', () =>
        HttpResponse.json({ projectId: 1, goldenCasesChecked: 1, patternsChecked: 0, refused: [] })),
    );

    await runRecheck();

    expect(await screen.findByText('Nothing refused')).toBeInTheDocument();
    expect(screen.getByText('Checked 1 active golden case and 0 approved learned patterns.')).toBeInTheDocument();
  });

  it('deactivates a refused golden case when the Admin chooses to, and runs the recheck again', async () => {
    mockLearning([]);
    let checks = 0;
    let body: unknown = null;
    mswServer.use(
      http.get('*/beacon/api/eval/grounding/recheck', () => {
        checks++;
        return HttpResponse.json({ projectId: 1, goldenCasesChecked: 1, patternsChecked: 0, refused: checks === 1 ? [REFUSED_CASE] : [] });
      }),
      http.put('*/beacon/api/eval/golden/:id', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await runRecheck();
    fireEvent.click(within(await findRow('Golden case #7')).getByRole('button', { name: 'Deactivate' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Deactivate golden case' })).getByRole('button', { name: 'Deactivate' }));

    await waitFor(() => expect(body).toEqual({ question: null, goldSql: null, isActive: false, notes: null }));
    expect(await screen.findByText('Nothing refused')).toBeInTheDocument();
    expect(checks).toBe(2);
  });

  it('sets a refused pattern back to pending with the hash the recheck read', async () => {
    mockLearning([]);
    let body: unknown = null;
    let checks = 0;
    mswServer.use(
      http.get('*/beacon/api/eval/grounding/recheck', () => {
        checks++;
        return HttpResponse.json({ projectId: 1, goldenCasesChecked: 0, patternsChecked: 1, refused: [REFUSED_PATTERN] });
      }),
      http.put('*/beacon/api/mcp/learned-patterns/:id/status', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await runRecheck();
    fireEvent.click(within(await findRow('Learned pattern #21')).getByRole('button', { name: 'Back to pending' }));

    await waitFor(() => expect(body).toEqual({ newStatus: McpPatternStatus.Pending, reviewedContentHash: 'f00d' }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Pattern #21 set back to pending.'));
    await waitFor(() => expect(checks).toBe(2));
  });

  it('says a pattern changed since the recheck read it', async () => {
    mockLearning([]);
    mswServer.use(
      http.get('*/beacon/api/eval/grounding/recheck', () =>
        HttpResponse.json({ projectId: 1, goldenCasesChecked: 0, patternsChecked: 1, refused: [REFUSED_PATTERN] })),
      http.put('*/beacon/api/mcp/learned-patterns/:id/status', () =>
        problem(409, 'The pattern changed since it was reviewed: reload the learned patterns and review it again.')),
    );

    await runRecheck();
    fireEvent.click(within(await findRow('Learned pattern #21')).getByRole('button', { name: 'Back to pending' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(CHANGED_SINCE_REVIEWED));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('hides the tabs and review actions the MCP gateway does not serve', async () => {
    mockLearning([pattern({ id: 1 })]);
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));

    renderWithProviders(<McpLearningPage />);
    await screen.findByText('orders');

    expect(screen.getByRole('tab', { name: /Learned patterns/ })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Problem tables' })).toBeInTheDocument();
    for (const name of [/Doc patches/, 'Golden candidates', 'Golden examples', 'Grounding recheck']) {
      await waitFor(() => expect(screen.queryByRole('tab', { name })).toBeNull());
    }
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    const dialog = await screen.findByRole('dialog', { name: 'Learned pattern' });
    expect(within(dialog).queryByRole('button', { name: 'Approve' })).toBeNull();
  });
});
