import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { McpEvalFailureTag } from '@/lib/enums';
import McpLearningPage from './McpLearningPage';
import { accuracyTrend } from './evalQueries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/** An Admin's eval runs: start a run over a project's golden cases, the runs with their accuracy, a run's results. */

const STATS = {
  totalSignals: 0, signals7d: 0, signals30d: 0, successRate: 0,
  patternsApproved: 0, patternsPending: 0, patternsRejected: 0, patchesApplied: 0, patchesProposed: 0, problemTables: [],
};

function run(overrides: Record<string, unknown>) {
  const merged = { totalCases: 10, passedCases: 8, ...overrides } as { totalCases: number; passedCases: number; executionAccuracy?: number };
  return {
    executionAccuracy: merged.totalCases > 0 ? merged.passedCases / merged.totalCases : 0,
    id: 1,
    projectId: 1,
    triggeredByUserId: 1,
    totalCases: 10,
    passedCases: 8,
    status: 'Completed',
    judgeEnabled: false,
    createdTime: '2026-10-20T08:00:00Z',
    ...overrides,
  } as Record<string, unknown>;
}

function serve(runs: unknown[]) {
  mswServer.use(
    permissionsHandler({ isAdmin: true }),
    http.get('*/beacon/api/mcp/learning-stats', () => HttpResponse.json(STATS)),
    http.get('*/beacon/api/mcp/learned-patterns', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    http.get('*/beacon/api/mcp/documentation-patches', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
    http.get('*/beacon/api/eval/runs', () => HttpResponse.json(runs)),
  );
}

async function openTab() {
  renderWithProviders(<McpLearningPage />);
  fireEvent.click(await screen.findByRole('tab', { name: 'Eval runs' }));
  expect(await screen.findByText('Choose a project')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Eval project' }));
  fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
}

describe('Eval runs tab (Admin)', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('is offered to an Admin only', async () => {
    serve([]);
    mswServer.use(permissionsHandler({ permissions: ['Approver'] }));

    renderWithProviders(<McpLearningPage />);
    await screen.findByRole('tab', { name: 'Learned patterns' });

    expect(screen.queryByRole('tab', { name: 'Eval runs' })).toBeNull();
  });

  it('lists the project runs with accuracy and the change since the previous finished run', async () => {
    serve([
      run({ id: 3, passedCases: 9, createdTime: '2026-10-22T08:00:00Z' }),
      run({ id: 2, status: 'Failed', totalCases: 0, passedCases: 0, createdTime: '2026-10-21T08:00:00Z' }),
      run({ id: 1, passedCases: 6 }),
    ]);

    await openTab();

    const table = within(await screen.findByRole('table', { name: 'Eval runs' }));
    const newest = within(table.getByText('#3').closest('tr') as HTMLElement);
    expect(newest.getByText('9 / 10')).toBeInTheDocument();
    expect(newest.getByText('90%')).toBeInTheDocument();
    expect(newest.getByText('+30 pts')).toBeInTheDocument();
    const failed = within(table.getByText('#2').closest('tr') as HTMLElement);
    expect(failed.getByText('Failed')).toBeInTheDocument();
    const oldest = within(table.getByText('#1').closest('tr') as HTMLElement);
    expect(oldest.getByText('60%')).toBeInTheDocument();
  });

  it('says so when the project has no runs', async () => {
    serve([]);

    await openTab();

    expect(await screen.findByText('No eval runs')).toBeInTheDocument();
  });

  it('asks before starting a run, then starts it for the chosen project', async () => {
    serve([]);
    const bodies: unknown[] = [];
    mswServer.use(
      http.post('*/beacon/api/eval/runs', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json({ runId: 9 });
      }),
      http.get('*/beacon/api/eval/runs/9/results', () => HttpResponse.json([])),
    );

    await openTab();
    await screen.findByText('No eval runs');
    fireEvent.click(screen.getByRole('button', { name: 'Run eval' }));
    expect(bodies).toEqual([]);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Run eval' }));

    await waitFor(() => expect(bodies).toEqual([{ projectId: 1 }]));
    expect(await screen.findByText('Run #9 results')).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith('Eval run #9 started.');
  });

  it('opens a run and shows each case with its outcome, failure kind, SQL and error', async () => {
    serve([run({ id: 4 })]);
    mswServer.use(
      http.get('*/beacon/api/eval/runs/4/results', () =>
        HttpResponse.json([
          { id: 1, evalRunId: 4, evalCaseId: 11, generatedSql: 'SELECT 1', passed: true, failureTag: McpEvalFailureTag.None, executionError: null, judgeUsed: false, judgeVerdict: null, resultRowCount: 1, executionTimeMs: 5 },
          { id: 2, evalRunId: 4, evalCaseId: 12, generatedSql: 'SELECT bad', passed: false, failureTag: McpEvalFailureTag.ExecutionError, executionError: 'column bad does not exist', judgeUsed: false, judgeVerdict: null, resultRowCount: null, executionTimeMs: 7 },
        ])),
    );

    await openTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Show results of run 4' }));

    const results = within(await screen.findByRole('table', { name: 'Results of run 4' }));
    await screen.findByText('#12');
    const failed = within(results.getByText('#12').closest('tr') as HTMLElement);
    expect(failed.getByText('Failed')).toBeInTheDocument();
    expect(failed.getByText('Execution error')).toBeInTheDocument();
    expect(failed.getByText('column bad does not exist')).toBeInTheDocument();
    expect(failed.getByText('SELECT bad')).toBeInTheDocument();
    expect(within(results.getByText('#11').closest('tr') as HTMLElement).getByText('Passed')).toBeInTheDocument();
  });

  it('caps the rendered results and says how many cases there are', async () => {
    serve([run({ id: 5 })]);
    const many = Array.from({ length: 250 }, (_, i) => ({
      id: i + 1, evalRunId: 5, evalCaseId: i + 1, generatedSql: 'SELECT 1', passed: true,
      failureTag: McpEvalFailureTag.None, executionError: null, judgeUsed: false, judgeVerdict: null, resultRowCount: 1, executionTimeMs: 1,
    }));
    mswServer.use(http.get('*/beacon/api/eval/runs/5/results', () => HttpResponse.json(many)));

    await openTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Show results of run 5' }));

    expect(await screen.findByText('250 cases, first 200 shown')).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Results of run 5' })).getAllByRole('row');
    expect(rows).toHaveLength(201);
  });
});

describe('accuracy of a run with harness errors', () => {
  it('shows the server execution accuracy, which leaves not-evaluated cases out', async () => {
    serve([run({ id: 7, totalCases: 10, passedCases: 8, executionAccuracy: 1 })]);

    await openTab();

    const table = within(await screen.findByRole('table', { name: 'Eval runs' }));
    expect(within(table.getByText('#7').closest('tr') as HTMLElement).getByText('100%')).toBeInTheDocument();
  });
});

describe('results of a run that is going', () => {
  it('are read again when the run completes', async () => {
    let status = 'Running';
    mswServer.use(
      permissionsHandler({ isAdmin: true }),
      http.get('*/beacon/api/mcp/learning-stats', () => HttpResponse.json(STATS)),
      http.get('*/beacon/api/mcp/learned-patterns', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
      http.get('*/beacon/api/mcp/documentation-patches', () => HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 })),
      http.get('*/beacon/api/eval/runs', () => HttpResponse.json([run({ id: 8, status })])),
      http.get('*/beacon/api/eval/runs/8/results', () =>
        HttpResponse.json(status === 'Running' ? [] : [
          { id: 1, evalRunId: 8, evalCaseId: 21, generatedSql: 'SELECT 1', passed: true, failureTag: McpEvalFailureTag.None, executionError: null, judgeUsed: false, judgeVerdict: null, resultRowCount: 1, executionTimeMs: 5 },
        ])),
    );

    await openTab();
    fireEvent.click(await screen.findByRole('button', { name: 'Show results of run 8' }));
    expect(await screen.findByText('No results yet')).toBeInTheDocument();

    status = 'Completed';

    expect(await screen.findByText('#21', undefined, { timeout: 8000 })).toBeInTheDocument();
  }, 15000);
});

describe('accuracyTrend', () => {
  it('compares each finished run with the next older finished run and leaves the rest empty', () => {
    const trend = accuracyTrend([
      run({ id: 3, passedCases: 5 }),
      run({ id: 2, status: 'Running' }),
      run({ id: 1, passedCases: 8 }),
    ] as never);

    expect(trend).toEqual([-30, null, null]);
  });
});
