import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { AlertTriangle } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { describeError } from '@/lib/api';
import { sortToParam, toggleSort, type SortState } from '@/lib/paging';
import { useQueryDetailQuery, usePreviewQueryMutation, type QueryPreviewResult } from './queries';
import { QueryHero } from './parts/QueryHero';
import { QueryKpiGrid } from './parts/QueryKpiGrid';
import { QueryPerfRow } from './parts/QueryPerfRow';
import { QueryInfoCard } from './parts/QueryInfoCard';
import { QueryStepsCard } from './parts/QueryStepsCard';
import { FinalQueryCard } from './parts/FinalQueryCard';
import { McpToolCard } from './parts/McpToolCard';
import { QueryTabsCard, type QueryTabKey } from './parts/QueryTabsCard';
import { RightRail } from './parts/RightRail';
import { QuerySaveBar } from './parts/QuerySaveBar';
import { QueryRunPanel } from './parts/QueryRunPanel';
import { AddSubscriptionDialog } from '@/routes/subscriptions/AddSubscriptionDialog';

export default function QueryDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const [tab, setTab] = useState<QueryTabKey>('subscriptions');
  const [previewOpen, setPreviewOpen] = useState(false);
  const [previewResult, setPreviewResult] = useState<QueryPreviewResult | null>(null);
  const [previewSort, setPreviewSort] = useState<SortState | null>(null);
  const [addSubOpen, setAddSubOpen] = useState(false);

  const validId = Number.isFinite(id) ? id : undefined;
  const detail = useQueryDetailQuery(validId);
  const query = detail.data;
  const previewMutation = usePreviewQueryMutation(validId);

  const editHref = `/queries/${id}/edit`;

  // Execute query can be pressed in the header or the sticky bottom bar, so bring the panel into
  // view when a run starts, and move focus to it when it finishes (Esc then closes it).
  const runPanelRef = useRef<HTMLDivElement>(null);
  const running = previewMutation.isPending;
  useEffect(() => {
    if (!previewOpen) return;
    runPanelRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
    if (!running) {
      runPanelRef.current?.focus({ preventScroll: true });
    }
  }, [previewOpen, running]);

  // Every page and every sort is a fresh server run that returns only that page plus a total count.
  const runPreview = useCallback(
    async (page: number, sort: SortState | null) => {
      setPreviewOpen(true);
      try {
        const result = await previewMutation.mutateAsync({ page, sort: sortToParam(sort) });
        setPreviewResult(result);
      } catch {
        // error already toasted by the mutation
      }
    },
    [previewMutation],
  );

  const onExecute = useCallback(async () => {
    setPreviewResult(null);
    setPreviewSort(null);
    await runPreview(0, null);
  }, [runPreview]);

  const onPreviewSort = useCallback(
    (column: string) => {
      const next = toggleSort(previewSort, column);
      setPreviewSort(next);
      void runPreview(0, next);
    },
    [previewSort, runPreview],
  );

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

      {previewOpen && (
        <QueryRunPanel
          ref={runPanelRef}
          running={previewMutation.isPending}
          result={previewResult}
          requestError={previewMutation.isError ? describeError(previewMutation.error, 'Query preview failed') : null}
          sort={previewSort}
          onPageChange={page => void runPreview(page, previewSort)}
          onSortChange={onPreviewSort}
          onRerun={() => void runPreview(previewResult?.result?.page ?? 0, previewSort)}
          onClose={() => setPreviewOpen(false)}
        />
      )}

      <QueryKpiGrid query={query} />
      <QueryPerfRow query={query} />

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px] items-start">
        <div className="flex flex-col gap-5 min-w-0">
          <QueryInfoCard query={query} />
          <QueryStepsCard query={query} editHref={editHref} />
          <FinalQueryCard query={query} />
          <McpToolCard query={query} />
          <QueryTabsCard query={query} tab={tab} onTabChange={setTab} />
        </div>
        <RightRail query={query} editHref={editHref} previewed={previewResult != null} />
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
