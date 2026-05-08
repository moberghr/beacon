import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { formatDateTime } from '@/lib/format';
import type { QueryDetail } from '../queries';

interface QueryHeroProps {
  query: QueryDetail;
  onExecute?: () => void;
  onAddSubscription?: () => void;
}

/**
 * Signal hero variant — eyebrow breadcrumb (status / step kind / source) +
 * verb-emphasis title + sub line. Mirrors the Beacon-design query-detail.jsx
 * page-hero treatment used on TaskDetailPage.
 */
export function QueryHero({ query, onExecute, onAddSubscription }: QueryHeroProps) {
  const sourceLabel = query.aiActorName ?? 'USER-DEFINED';
  const stepLabel = query.isMultiStep ? 'multi-step' : 'single-step';
  const dataSourceText = query.dataSourceNames.length > 0
    ? query.dataSourceNames.join(', ')
    : '—';

  return (
    <div className="page-hero">
      <div className="page-hero__inner">
        <div className="page-hero__main">
          <div className="page-hero__eyebrow">
            <Link to="/queries" style={{ color: 'inherit', textDecoration: 'none' }}>Queries</Link>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#{query.id}</span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--ok"><span className="pill__dot" />ACTIVE</span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>
              {stepLabel.toUpperCase()}
            </span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--neutral"><span className="pill__dot" />{sourceLabel.toUpperCase()}</span>
            {query.isLocked && (
              <>
                <span className="beacon-hero__sep">·</span>
                <span className="pill pill--warn"><Icon.Lock size={10} /> LOCKED</span>
              </>
            )}
          </div>
          <h1 className="page-hero__title">
            Editing <span className="page-hero__word">{query.name}</span>
          </h1>
          <p className="page-hero__sub">
            {stepLabel} query against <span className="mono">{dataSourceText}</span>
            {' · '}created <span className="mono">{formatDateTime(query.createdTime)}</span>
            {' · '}<span className="mono">{query.totalExecutions.toLocaleString()}</span> run{query.totalExecutions === 1 ? '' : 's'} to date
          </p>
        </div>
        <div className="page-hero__actions">
          {onAddSubscription && (
            <button type="button" className="btn" onClick={onAddSubscription}>
              <Icon.Plus size={14} className="btn__icon" /> Add subscription
            </button>
          )}
          {onExecute && (
            <button type="button" className="btn btn--primary" onClick={onExecute}>
              <Icon.Bolt size={14} className="btn__icon" /> Execute query
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
