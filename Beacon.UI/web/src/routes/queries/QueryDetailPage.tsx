import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { AlertTriangle } from 'lucide-react';
import { Card, CardBody, Button } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { useQueryDetailQuery, usePreviewQueryMutation, type QueryExecutionPreviewResult } from './queries';
import { QueryHero } from './parts/QueryHero';
import { QueryKpiGrid } from './parts/QueryKpiGrid';
import { QueryPerfRow } from './parts/QueryPerfRow';
import { QueryInfoCard } from './parts/QueryInfoCard';
import { QueryStepsCard } from './parts/QueryStepsCard';
import { FinalQueryCard } from './parts/FinalQueryCard';
import { QueryTabsCard, type QueryTabKey } from './parts/QueryTabsCard';
import { RightRail } from './parts/RightRail';
import { QuerySaveBar } from './parts/QuerySaveBar';
import { PreviewResultsCard } from './parts/PreviewResultsCard';
import { AddSubscriptionDialog } from '@/routes/subscriptions/AddSubscriptionDialog';

export default function QueryDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const [tab, setTab] = useState<QueryTabKey>('subscriptions');
  const [previewOpen, setPreviewOpen] = useState(false);
  const [previewResult, setPreviewResult] = useState<QueryExecutionPreviewResult | null>(null);
  const [addSubOpen, setAddSubOpen] = useState(false);

  const validId = Number.isFinite(id) ? id : undefined;
  const detail = useQueryDetailQuery(validId);
  const query = detail.data;
  const previewMutation = usePreviewQueryMutation(validId);

  const editHref = `/queries/${id}/edit`;

  const onExecute = useCallback(async () => {
    setPreviewOpen(true);
    setPreviewResult(null);
    try {
      const result = await previewMutation.mutateAsync();
      setPreviewResult(result);
    } catch {
      // error already toasted by the mutation
    }
  }, [previewMutation]);

  const onAddSubscription = useCallback(() => {
    setAddSubOpen(true);
  }, []);

  // Auto-run when arriving with ?run=1 (from NewQueryPage "Save & run").
  const [searchParams, setSearchParams] = useSearchParams();
  const autoRunFiredRef = useRef(false);
  useEffect(() => {
    if (autoRunFiredRef.current) return;
    if (searchParams.get('run') !== '1') return;
    if (!query) return;
    autoRunFiredRef.current = true;
    const next = new URLSearchParams(searchParams);
    next.delete('run');
    setSearchParams(next, { replace: true });
    void onExecute();
  }, [query, searchParams, setSearchParams, onExecute]);

  // Cmd/Ctrl+Enter triggers execute modal.
  useEffect(() => {
    if (!query) return;
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
        const target = e.target as HTMLElement | null;
        const tag = target?.tagName?.toLowerCase();
        if (tag === 'input' || tag === 'textarea' || target?.isContentEditable) return;
        e.preventDefault();
        onExecute();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [query, onExecute]);

  if (!Number.isFinite(id)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState icon={<AlertTriangle size={20} />} title="Invalid query id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load query"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !query) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <div className="text-text-muted">Loading query…</div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-5 p-7" data-screen-label="03 Query Detail">
      <QueryHero
        query={query}
        onExecute={onExecute}
        onAddSubscription={onAddSubscription}
      />

      <QueryKpiGrid query={query} />
      <QueryPerfRow query={query} />

      {previewOpen && (
        <div className="flex flex-col gap-3">
          {previewMutation.isPending && (
            <Card>
              <CardBody>
                <div className="text-text-muted">Running query…</div>
              </CardBody>
            </Card>
          )}
          {!previewMutation.isPending && previewResult && (
            <>
              {previewResult.stepResults.length > 1 && previewResult.stepResults.map(sr => (
                <PreviewResultsCard
                  key={sr.stepOrder}
                  title={`Step ${sr.stepOrder}: ${sr.stepName}`}
                  rows={sr.previewResults ?? []}
                  totalRows={sr.totalRows}
                  executionTimeMs={sr.executionTimeMs}
                  error={sr.errorMessage}
                  onClose={() => setPreviewOpen(false)}
                />
              ))}
              {previewResult.finalResult && (
                <PreviewResultsCard
                  title="Final result"
                  rows={previewResult.finalResult.rows ?? []}
                  totalRows={previewResult.finalResult.rowCount}
                  executionTimeMs={previewResult.totalExecutionTimeMs}
                  error={previewResult.finalResult.error}
                  onClose={() => setPreviewOpen(false)}
                />
              )}
              {!previewResult.finalResult && previewResult.stepResults.length === 1 && (
                <PreviewResultsCard
                  title="Query result"
                  rows={previewResult.stepResults[0].previewResults ?? []}
                  totalRows={previewResult.stepResults[0].totalRows}
                  executionTimeMs={previewResult.totalExecutionTimeMs}
                  error={previewResult.errorMessage}
                  onClose={() => setPreviewOpen(false)}
                />
              )}
              {previewResult.stepResults.length === 0 && (
                <Card>
                  <CardBody>
                    <div className="text-text-muted">No results returned.</div>
                    <Button type="button" onClick={() => setPreviewOpen(false)} className="mt-2">
                      Close
                    </Button>
                  </CardBody>
                </Card>
              )}
            </>
          )}
          {!previewMutation.isPending && !previewResult && (
            <Card>
              <CardBody>
                <div className="text-text-muted">Preview failed — see the toast for details.</div>
                <Button type="button" onClick={() => setPreviewOpen(false)} className="mt-2">
                  Close
                </Button>
              </CardBody>
            </Card>
          )}
        </div>
      )}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
          <QueryInfoCard query={query} />
          <QueryStepsCard query={query} editHref={editHref} />
          <FinalQueryCard query={query} />
          <QueryTabsCard query={query} tab={tab} onTabChange={setTab} />
        </div>
        <RightRail query={query} editHref={editHref} />
      </div>

      <QuerySaveBar
        query={query}
        editHref={editHref}
        onExecute={onExecute}
        executePending={previewMutation.isPending}
      />

      <AddSubscriptionDialog
        open={addSubOpen}
        onClose={() => setAddSubOpen(false)}
        initialQueryId={id}
      />
    </div>
  );
}
