import { useEffect, useRef, useState } from 'react';
import { Button, Card, CardBody, CardHead, CardSub, CardTitle, Pill } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatDateTime } from '@/lib/format';
import { showHiddenCharacters } from '@/lib/hiddenCharacters';
import { McpEvalFailureTag } from '@/lib/enums';
import { ProjectPicker } from '../projects/ProjectPicker';
import {
  EVAL_RESULTS_RENDER_CAP,
  EVAL_STATUS_RUNNING,
  FAILURE_TAG_LABEL,
  accuracyPercent,
  accuracyTrend,
  useEvalResults,
  useEvalRuns,
  useStartEvalRun,
  type EvalRunListItem,
} from './evalQueries';

/**
 * Admin only: starts an eval run over one project's golden cases, lists the project's runs with their accuracy and its
 * change since the previous finished run, and opens a run's per-case results. A run executes SQL on the project's data
 * sources, so starting one asks first.
 */
export function EvalRunsTab() {
  const [project, setProject] = useState<{ id: number; name: string } | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [openRun, setOpenRun] = useState<number | null>(null);
  const projectId = project?.id ?? null;
  const runs = useEvalRuns(projectId);
  const start = useStartEvalRun();
  const items = runs.data ?? [];
  const trend = accuracyTrend(items);
  const running = items.some(x => x.status === EVAL_STATUS_RUNNING);

  function confirmStart() {
    if (projectId === null) return;
    start.mutate(projectId, {
      onSuccess: result => {
        setConfirming(false);
        setOpenRun(result.runId);
      },
      onError: () => setConfirming(false),
    });
  }

  return (
    <>
      <p className="m-0 mb-3 text-sm text-text-muted">
        An eval run asks the project&apos;s active golden examples again and compares what comes back with the saved SQL. It
        runs in the background; the list refreshes until it is done.
      </p>
      <div className="mb-3 flex gap-2">
        <ProjectPicker
          value={projectId}
          selectedLabel={project?.name}
          ariaLabel="Eval project"
          placeholder="Choose a project…"
          onSelect={x => {
            setProject(x ? { id: x.id, name: x.name } : null);
            setOpenRun(null);
          }}
        />
        <Button variant="primary" type="button" disabled={projectId === null || start.isPending} onClick={() => setConfirming(true)}>
          Run eval
        </Button>
      </div>

      {projectId === null ? (
        <EmptyState title="Choose a project" description="Eval runs belong to one project's golden examples." />
      ) : runs.isLoading ? (
        <p className="text-text-muted">Loading runs…</p>
      ) : runs.isError ? (
        <EmptyState title="The runs could not be read" description={runs.error instanceof Error ? runs.error.message : 'Unknown error'} />
      ) : items.length === 0 ? (
        <EmptyState title="No eval runs" description="Run the eval to measure how the golden examples do today." />
      ) : (
        <table className="w-full text-sm" aria-label="Eval runs">
          <thead>
            <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
              <th className="px-2 py-2">Run</th>
              <th className="px-2 py-2">Started</th>
              <th className="px-2 py-2">Status</th>
              <th className="px-2 py-2">Passed</th>
              <th className="px-2 py-2">Accuracy</th>
              <th className="px-2 py-2">Since previous</th>
              <th className="px-2 py-2" />
            </tr>
          </thead>
          <tbody>
            {items.map((run, index) => (
              <RunRow key={run.id} run={run} change={trend[index]} open={openRun === run.id} onToggle={() => setOpenRun(openRun === run.id ? null : (run.id ?? null))} />
            ))}
          </tbody>
        </table>
      )}
      {running && <p className="mt-2 text-2xs text-text-muted">A run is in progress; this list refreshes by itself.</p>}

      {openRun !== null && <RunResults runId={openRun} running={items.some(x => x.id === openRun && x.status === EVAL_STATUS_RUNNING)} />}

      <ConfirmDialog
        open={confirming}
        title="Run eval"
        message={`Run every active golden example of ${project?.name ?? 'this project'}? Each one is asked again and its SQL is executed on the project's data sources.`}
        confirmLabel="Run eval"
        busy={start.isPending}
        onConfirm={confirmStart}
        onCancel={() => setConfirming(false)}
      />
    </>
  );
}

function RunRow({ run, change, open, onToggle }: { run: EvalRunListItem; change: number | null; open: boolean; onToggle: () => void }) {
  const percent = accuracyPercent(run);
  const tone = run.status === 'Completed' ? 'ok' : run.status === 'Failed' ? 'crit' : 'warn';

  return (
    <tr className="border-b border-border last:border-b-0">
      <td className="px-2 py-2 mono text-xs">#{run.id}</td>
      <td className="px-2 py-2 text-text-muted">{formatDateTime(run.createdTime)}</td>
      <td className="px-2 py-2">
        <Pill tone={tone}>{run.status}</Pill>
      </td>
      <td className="px-2 py-2 mono text-xs">
        {run.passedCases ?? 0} / {run.totalCases ?? 0}
      </td>
      <td className="px-2 py-2">{percent === null ? '—' : `${percent}%`}</td>
      <td className="px-2 py-2 mono text-xs" aria-label={change === null ? 'No earlier finished run' : `Change ${change} points`}>
        {change === null ? '—' : change === 0 ? '±0' : `${change > 0 ? '+' : ''}${change} pts`}
      </td>
      <td className="px-2 py-2 text-right">
        <Button size="sm" type="button" aria-label={`${open ? 'Hide' : 'Show'} results of run ${run.id}`} onClick={onToggle}>
          {open ? 'Hide results' : 'Results'}
        </Button>
      </td>
    </tr>
  );
}

function RunResults({ runId, running }: { runId: number; running: boolean }) {
  const results = useEvalResults(runId, running);
  const wasRunning = useRef(running);
  const { refetch } = results;
  useEffect(() => {
    // The run just ended: its results were written then, so read them now rather than at the next poll.
    if (wasRunning.current && !running) void refetch();
    wasRunning.current = running;
  }, [running, refetch]);
  const all = results.data ?? [];
  const shown = all.slice(0, EVAL_RESULTS_RENDER_CAP);

  return (
    <Card className="mt-4">
      <CardHead>
        <CardTitle>Run #{runId} results</CardTitle>
        <CardSub>
          {results.isLoading ? 'Loading…' : `${all.length} ${all.length === 1 ? 'case' : 'cases'}${all.length > shown.length ? `, first ${shown.length} shown` : ''}`}
        </CardSub>
      </CardHead>
      <CardBody>
        {results.isError ? (
          <EmptyState title="The results could not be read" description={results.error instanceof Error ? results.error.message : 'Unknown error'} />
        ) : !results.isLoading && all.length === 0 ? (
          <EmptyState title="No results yet" description="A run records its cases when it ends; this refreshes by itself until then." />
        ) : (
          <table className="w-full text-sm" aria-label={`Results of run ${runId}`}>
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-eyebrow text-text-muted border-b border-border">
                <th className="px-2 py-2">Case</th>
                <th className="px-2 py-2">Outcome</th>
                <th className="px-2 py-2">Failure</th>
                <th className="px-2 py-2">Rows</th>
                <th className="px-2 py-2">Generated SQL / error</th>
              </tr>
            </thead>
            <tbody>
              {shown.map(x => (
                <tr key={x.id} className="border-b border-border last:border-b-0 align-top">
                  <td className="px-2 py-2 mono text-xs">#{x.evalCaseId}</td>
                  <td className="px-2 py-2">{x.passed ? <Pill tone="ok">Passed</Pill> : <Pill tone="crit">Failed</Pill>}</td>
                  <td className="px-2 py-2">
                    {x.failureTag === undefined || x.failureTag === McpEvalFailureTag.None ? '—' : (FAILURE_TAG_LABEL[x.failureTag] ?? x.failureTag)}
                  </td>
                  <td className="px-2 py-2 mono text-xs">{x.resultRowCount ?? '—'}</td>
                  <td className="px-2 py-2 max-w-[520px]">
                    {x.executionError && <div className="text-crit">{showHiddenCharacters(x.executionError)}</div>}
                    {x.generatedSql ? (
                      <pre className="m-0 whitespace-pre-wrap break-words mono text-2xs">{showHiddenCharacters(x.generatedSql)}</pre>
                    ) : (
                      !x.executionError && <span className="text-text-muted">—</span>
                    )}
                    {x.judgeUsed && x.judgeVerdict && <div className="text-2xs text-text-muted">Judge: {x.judgeVerdict}</div>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </CardBody>
    </Card>
  );
}
