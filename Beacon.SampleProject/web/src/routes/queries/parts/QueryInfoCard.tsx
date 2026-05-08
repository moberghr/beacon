import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
import { formatDateTime } from '@/lib/format';
import type { QueryDetail } from '../queries';

export function QueryInfoCard({ query }: { query: QueryDetail }) {
  const queryTypeLabel = query.isMultiStep
    ? `multi-step (${query.steps.length} steps)`
    : 'single-step';
  const dataSourcesText = query.dataSourceNames.length > 0
    ? query.dataSourceNames.join(' · ')
    : '—';

  return (
    <div className="card">
      <div className="card__head">
        <Icon.Info size={15} className="muted" />
        <h3 className="card__title">Query information</h3>
      </div>
      <div className="card__body">
        <div className="kv">
          <KV label="Query ID" value={<span className="mono">#{query.id}</span>} />
          <KV
            label="Query type"
            value={<span className="pill pill--neutral mono">{queryTypeLabel}</span>}
          />
          <KV
            label="Created"
            value={<span className="mono">{formatDateTime(query.createdTime)}</span>}
          />
          <KV
            label="Data sources"
            value={<span className="mono">{dataSourcesText}</span>}
          />
          <KV
            label="Cross-source"
            value={query.isCrossDataSource
              ? <span className="pill pill--warn"><span className="pill__dot" />yes</span>
              : <span className="subtle">no</span>}
          />
          <KV
            label="Cross-database"
            value={query.isCrossDatabase
              ? <span className="pill pill--warn"><span className="pill__dot" />yes</span>
              : <span className="subtle">no</span>}
          />
          <KV
            label="Owner"
            value={query.aiActorName
              ? <span className="mono">{query.aiActorName} · AI</span>
              : <span className="mono">user-defined</span>}
          />
          <KV
            label="Lock state"
            value={query.isLocked
              ? <span className="pill pill--warn"><Icon.Lock size={10} /> locked</span>
              : <span className="pill pill--ok"><span className="pill__dot" />unlocked</span>}
          />
          <KV
            label="Description"
            value={query.description
              ? <span>{query.description}</span>
              : <span className="subtle">—</span>}
          />
          <KV
            label="Total executions"
            value={<span className="mono">{query.totalExecutions.toLocaleString()}</span>}
          />
          <KV
            label="Notifications sent"
            value={<span className="mono">{query.sentNotifications.toLocaleString()}</span>}
          />
        </div>
      </div>
    </div>
  );
}

function KV({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="kv__row">
      <span className="kv__label">{label}</span>
      <span className="kv__value">{value}</span>
    </div>
  );
}
