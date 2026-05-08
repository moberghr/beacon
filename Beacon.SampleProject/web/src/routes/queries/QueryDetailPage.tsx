import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { useQueryDetailQuery } from './queries';
import { QueryHero } from './parts/QueryHero';
import { QueryKpiGrid } from './parts/QueryKpiGrid';
import { QueryPerfRow } from './parts/QueryPerfRow';
import { QueryInfoCard } from './parts/QueryInfoCard';
import { QueryStepsCard } from './parts/QueryStepsCard';
import { FinalQueryCard } from './parts/FinalQueryCard';
import { QueryTabsCard, type QueryTabKey } from './parts/QueryTabsCard';
import { RightRail } from './parts/RightRail';
import { QuerySaveBar } from './parts/QuerySaveBar';

/**
 * Read-mostly query detail. Step CRUD and inline execution still live in the
 * Blazor `/beacon/queries/{id}` editor (5f). The lock toggle and tab content
 * are wired to real backend handlers.
 */
export default function QueryDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const [tab, setTab] = useState<QueryTabKey>('subscriptions');

  const validId = Number.isFinite(id) ? id : undefined;
  const detail = useQueryDetailQuery(validId);
  const query = detail.data;

  const legacyEditHref = `/beacon/queries/${id}`;
  const legacyExecuteHref = `/beacon/queries/${id}`;
  const legacyAddSubscriptionHref = `/beacon/subscriptions/add/${id}`;

  const onExecute = useCallback(() => {
    window.location.href = legacyExecuteHref;
  }, [legacyExecuteHref]);

  const onAddSubscription = useCallback(() => {
    window.location.href = legacyAddSubscriptionHref;
  }, [legacyAddSubscriptionHref]);

  // ⌘↵ executes — mirrors the kbd hint in the save bar.
  useEffect(() => {
    if (!query) return;
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
        const target = e.target as HTMLElement | null;
        const tag = target?.tagName?.toLowerCase();
        if (tag === 'input' || tag === 'textarea' || target?.isContentEditable) return;
        e.preventDefault();
        window.location.href = legacyExecuteHref;
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [query, legacyExecuteHref]);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid query id" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="page">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load query"
          description={detail.error instanceof Error ? detail.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (detail.isLoading || !query) {
    return (
      <div className="page">
        <div className="muted">Loading query…</div>
      </div>
    );
  }

  return (
    <div className="page" data-screen-label="03 Query Detail">
      <QueryHero
        query={query}
        onExecute={onExecute}
        onAddSubscription={onAddSubscription}
      />

      <QueryKpiGrid query={query} />
      <QueryPerfRow query={query} />

      <div className="q-layout">
        <div className="q-section">
          <QueryInfoCard query={query} />
          <QueryStepsCard query={query} legacyEditHref={legacyEditHref} />
          <FinalQueryCard query={query} />
          <QueryTabsCard query={query} tab={tab} onTabChange={setTab} />
        </div>
        <RightRail query={query} legacyEditHref={legacyEditHref} />
      </div>

      <QuerySaveBar
        query={query}
        legacyEditHref={legacyEditHref}
        legacyExecuteHref={legacyExecuteHref}
      />
    </div>
  );
}
