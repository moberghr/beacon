import type { ReactNode } from 'react';
import { Info, Lock } from 'lucide-react';
import { Card, CardHead, CardTitle, CardBody, Pill } from '@/components/beacon';
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
    <Card>
      <CardHead>
        <Info className="size-3.5 text-text-muted" />
        <CardTitle>Query information</CardTitle>
      </CardHead>
      <CardBody>
        <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
          <KV label="Query ID" value={<span className="mono">#{query.id}</span>} />
          <KV
            label="Query type"
            value={<Pill className="mono normal-case tracking-normal">{queryTypeLabel}</Pill>}
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
              ? <Pill tone="warn" dot>yes</Pill>
              : <span className="text-text-subtle">no</span>}
          />
          <KV
            label="Cross-database"
            value={query.isCrossDatabase
              ? <Pill tone="warn" dot>yes</Pill>
              : <span className="text-text-subtle">no</span>}
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
              ? <Pill tone="warn"><Lock size={10} /> locked</Pill>
              : <Pill tone="ok" dot>unlocked</Pill>}
          />
          <KV
            label="Description"
            value={query.description
              ? <span>{query.description}</span>
              : <span className="text-text-subtle">—</span>}
          />
          <KV
            label="Total executions"
            value={<span className="mono">{query.totalExecutions.toLocaleString()}</span>}
          />
          <KV
            label="Notifications sent"
            value={<span className="mono">{query.sentNotifications.toLocaleString()}</span>}
          />
        </dl>
      </CardBody>
    </Card>
  );
}

function KV({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1 border-b border-dashed border-border last:border-b-0">
      <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</dt>
      <dd className="text-sm text-right">{value}</dd>
    </div>
  );
}
