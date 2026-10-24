import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';
import { McpEvalFailureTag } from '@/lib/enums';
import type { EvalResultItem, EvalRunListItem, RunEvalResult } from '@/api/generated/beacon-api';

export type { EvalResultItem, EvalRunListItem };

export const EVAL_RUNS_KEY = ['eval', 'runs'] as const;
export const EVAL_RESULTS_KEY = ['eval', 'results'] as const;

/** The most recent runs one list reads (the endpoint takes a count, not a page). */
export const EVAL_RUNS_TAKE = 50;
/** The most results one drill-down renders; the run's own counts say how many cases it has. */
export const EVAL_RESULTS_RENDER_CAP = 200;

export const EVAL_STATUS_RUNNING = 'Running';

export const FAILURE_TAG_LABEL: Record<number, string> = {
  [McpEvalFailureTag.None]: 'None',
  [McpEvalFailureTag.RetrievalFailure]: 'Retrieval',
  [McpEvalFailureTag.SqlReasoningFailure]: 'SQL reasoning',
  [McpEvalFailureTag.ExecutionError]: 'Execution error',
  [McpEvalFailureTag.HarnessError]: 'Harness error',
};

/**
 * One project's eval runs, newest first (Admin). Read once a project is chosen; while a run is still going the list is
 * read again every few seconds, because the run is executed in the background.
 */
export function useEvalRuns(projectId: number | null) {
  return useQuery({
    queryKey: [...EVAL_RUNS_KEY, projectId],
    queryFn: async () =>
      unwrap<EvalRunListItem[]>(await beaconApi().getEvalRuns(projectId as number, EVAL_RUNS_TAKE)),
    enabled: projectId !== null,
    refetchInterval: query => (query.state.data?.some(x => x.status === EVAL_STATUS_RUNNING) ? 3000 : false),
  });
}

/** The per-case results of one run (Admin), in case order. Read when a run is opened. */
export function useEvalResults(runId: number | null, runRunning = false) {
  return useQuery({
    queryKey: [...EVAL_RESULTS_KEY, runId],
    queryFn: async () => unwrap<EvalResultItem[]>(await beaconApi().getEvalResults(runId as number)),
    enabled: runId !== null,
    // The server writes a run's results when it ends, so a run still going has none yet.
    refetchInterval: runRunning ? 3000 : false,
  });
}

/** Starts a run over a project's golden cases (Admin). The server answers at once with the run's id; it executes in the background. */
export function useStartEvalRun() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, RunEvalResult>({
      qc,
      mutationFn: async projectId => unwrap<RunEvalResult>(await beaconApi().runEval({ projectId })),
      invalidate: [EVAL_RUNS_KEY, EVAL_RESULTS_KEY],
      successMsg: (_, result) => `Eval run #${result.runId} started.`,
      errorFallback: 'Starting the eval run failed',
    }),
  );
}

/**
 * The server's execution accuracy (0 to 1: passed over the cases actually evaluated, harness errors left out so an
 * outage does not read as a regression) in whole percent; null for a run with no cases.
 */
export function accuracyPercent(run: { totalCases?: number; executionAccuracy?: number }): number | null {
  return (run.totalCases ?? 0) > 0 ? Math.round((run.executionAccuracy ?? 0) * 100) : null;
}

/**
 * Percentage-point change of each run against the next older finished run of the list (newest first). Null for the
 * oldest run, a run that is not completed, or one without cases.
 */
export function accuracyTrend(runs: EvalRunListItem[]): (number | null)[] {
  return runs.map((run, index) => {
    const current = run.status === 'Completed' ? accuracyPercent(run) : null;
    if (current === null) return null;
    const older = runs.slice(index + 1).find(x => x.status === 'Completed' && accuracyPercent(x) !== null);
    return older === undefined ? null : current - (accuracyPercent(older) as number);
  });
}
