import { forwardRef, useEffect, useState } from "react";
import { Check, Copy, RotateCw, X } from "lucide-react";
import {
  Banner,
  Button,
  Card,
  CardActions,
  CardBody,
  CardHead,
  CardTitle,
  Kbd,
  Pill,
  Seg,
} from "@/components/beacon";
import { Pager } from "@/components/data/Pager";
import { cn } from "@/lib/cn";
import type { SortState } from "@/lib/paging";
import type {
  PreviewRow,
  QueryPreviewResult,
  QueryResultPage,
} from "../queries";
import { ResultsTable, rowsToTsv } from "./ResultsTable";

interface QueryRunPanelProps {
  running: boolean;
  result: QueryPreviewResult | null;
  /** The request itself failed (network, 4xx/5xx) — distinct from a query that ran and failed. */
  requestError: string | null;
  /** The sort the result was requested with; the server echoes whether it applied. */
  sort: SortState | null;
  /** Re-runs the preview for another page (zero-based). */
  onPageChange: (page: number) => void;
  /** Re-runs the preview sorted by a result column. */
  onSortChange: (column: string) => void;
  onRerun: () => void;
  onClose: () => void;
  /** Accessible name of the region; the editor shows step and query previews in the same panel. */
  label?: string;
}

interface ResultView {
  key: string;
  label: string;
  rows: PreviewRow[];
  totalRows: number;
  error: string | null;
  /** The paged result; step views of a multi-step query only carry their first rows. */
  page: QueryResultPage | null;
}

/**
 * The outcome of a query or step preview. The result is paged on the server: each page (and each sort)
 * re-runs the preview and fetches only that page plus an exact row count. Intermediate steps of a
 * multi-step query show their first rows — they run in full only to feed the final query. Previews are
 * not recorded as executions.
 */
export const QueryRunPanel = forwardRef<HTMLDivElement, QueryRunPanelProps>(
  function QueryRunPanel(
    {
      running,
      result,
      requestError,
      sort,
      onPageChange,
      onSortChange,
      onRerun,
      onClose,
      label = "Query run result",
    },
    ref,
  ) {
    const views = result ? buildViews(result) : [];
    const [viewKey, setViewKey] = useState<string | null>(null);
    const view =
      views.find((x) => x.key === viewKey) ??
      views.find((x) => x.key === "result") ??
      views[views.length - 1] ??
      null;
    const failed =
      !running && (requestError != null || (result != null && !result.success));
    const failure =
      requestError ??
      result?.errorMessage ??
      views.find((x) => x.error)?.error ??
      "Query preview failed.";

    // A new run starts on its result view again (not on every page of the same run).
    const runKey = result
      ? `${result.steps.length}:${result.dataSourcesInvolved.join()}`
      : null;
    useEffect(() => {
      setViewKey(null);
    }, [runKey]);

    const page = view?.page ?? null;

    return (
      <div
        ref={ref}
        tabIndex={-1}
        role="region"
        aria-label={label}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            e.stopPropagation();
            onClose();
          }
        }}
        className="scroll-mt-4 rounded-md outline-none focus-visible:ring-2 focus-visible:ring-brand-500/40"
      >
        <Card className="border-brand-500 ring-1 ring-brand-500/20">
          <CardHead>
            <span
              className={`size-2 rounded-full ${running ? "bg-brand-500 animate-pulse" : failed ? "bg-crit" : "bg-ok"}`}
            />
            <CardTitle>{running ? "Running query…" : "Last run"}</CardTitle>
            {result && (
              <span className="text-xs text-text-muted">
                {result.dataSourcesInvolved.join(", ")}
              </span>
            )}
            <CardActions>
              <span className="hidden md:inline-flex items-center gap-1 text-2xs text-text-subtle">
                <Kbd>Esc</Kbd> to close
              </span>
              <Button size="sm" onClick={onRerun} disabled={running}>
                <RotateCw className="size-3.5" />
                Re-run
              </Button>
              {view && !failed && view.rows.length > 0 && (
                <CopyRowsButton rows={view.rows} />
              )}
              <Button
                size="sm"
                variant="ghost"
                onClick={onClose}
                aria-label="Close run result"
              >
                <X className="size-3.5" />
              </Button>
            </CardActions>
          </CardHead>
          <CardBody flush>
            {running && !result && (
              <div className="p-4 text-sm text-text-muted">
                Waiting for the data source…
              </div>
            )}

            {failed && (
              <div className="p-4">
                <Banner tone="crit" title={failure} role="alert" />
              </div>
            )}

            {!failed && view && (
              <div
                className={cn(running && "opacity-60 transition-opacity")}
                aria-busy={running || undefined}
              >
                <div className="flex flex-wrap items-center gap-2 px-4 py-2.5 border-b border-border text-xs">
                  <Pill tone="ok" dot>
                    {view.totalRows.toLocaleString()}{" "}
                    {view.totalRows === 1 ? "row" : "rows"}
                  </Pill>
                  {result && (
                    <span className="mono text-text-muted">
                      {formatDuration(result.totalExecutionTimeMs)}
                    </span>
                  )}
                  <span className="text-text-subtle">
                    · preview, not recorded as an execution
                  </span>
                  {page && !page.sortable && (
                    <span className="text-text-subtle">
                      · this query's shape cannot be sorted by column
                    </span>
                  )}
                  {views.length > 1 && (
                    <Seg
                      className="ml-auto"
                      ariaLabel="Result to show"
                      options={views.map((x) => ({
                        value: x.key,
                        label: x.label,
                      }))}
                      value={view.key}
                      onChange={setViewKey}
                    />
                  )}
                </div>
                {view.error ? (
                  <div className="p-4">
                    <Banner tone="crit" title={view.error} role="alert" />
                  </div>
                ) : view.rows.length === 0 ? (
                  <div className="p-4 text-sm text-text-muted">
                    No rows returned.
                  </div>
                ) : (
                  <ResultsTable
                    rows={view.rows}
                    maxHeightClass="max-h-[60vh]"
                    rowOffset={page ? page.page * page.pageSize : 0}
                    sort={page?.sortable ? sort : null}
                    onSortChange={
                      page?.sortable && !running ? onSortChange : undefined
                    }
                  />
                )}
                {page ? (
                  <Pager
                    page={page.page}
                    pageSize={page.pageSize}
                    pageCount={page.pageCount}
                    totalCount={page.totalCount}
                    onPageChange={(x) => !running && onPageChange(x)}
                    className="border-t border-border"
                  />
                ) : (
                  view.totalRows > view.rows.length && (
                    <div className="px-4 py-2 text-xs text-text-muted border-t border-border">
                      First {view.rows.length} of{" "}
                      {view.totalRows.toLocaleString()} rows. Steps run in full
                      to feed the final query; only the final result is paged.
                    </div>
                  )
                )}
              </div>
            )}

            {!running && !failed && !view && (
              <div className="p-4 text-sm text-text-muted">
                No results returned.
              </div>
            )}
          </CardBody>
        </Card>
      </div>
    );
  },
);

function CopyRowsButton({ rows }: { rows: PreviewRow[] }) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(false), 1500);
    return () => window.clearTimeout(timer);
  }, [copied]);

  return (
    <Button
      size="sm"
      onClick={async () => {
        await navigator.clipboard.writeText(rowsToTsv(rows));
        setCopied(true);
      }}
    >
      {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
      {copied ? "Copied" : "Copy"}
    </Button>
  );
}

// The paged result first; a multi-step query adds one view per step with its first rows.
function buildViews(result: QueryPreviewResult): ResultView[] {
  const views: ResultView[] = [];
  const multiStep = result.steps.length > 1;

  if (result.result) {
    views.push({
      key: "result",
      label: multiStep ? "Final" : "Result",
      rows: result.result.rows,
      totalRows: result.result.totalCount,
      error: null,
      page: result.result,
    });
  } else if (!multiStep && result.steps.length === 1) {
    const step = result.steps[0];
    views.push({
      key: "result",
      label: "Result",
      rows: step.previewRows,
      totalRows: step.totalRows,
      error: step.success ? null : (step.errorMessage ?? "Query failed."),
      page: null,
    });
  }

  if (multiStep) {
    for (const step of result.steps) {
      views.push({
        key: `step-${step.stepOrder}`,
        label: `Step ${step.stepOrder}`,
        rows: step.previewRows,
        totalRows: step.totalRows,
        error: step.success ? null : (step.errorMessage ?? "Step failed."),
        page: null,
      });
    }
  }

  return views;
}

function formatDuration(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`;
}
