import { Link } from 'react-router-dom';
import { Bell, Lock, Zap } from 'lucide-react';
import { PageHeader, Button, Pill } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import type { QueryDetail } from '../queries';

interface QueryHeroProps {
  query: QueryDetail;
  onExecute?: () => void;
  onAddSubscription?: () => void;
}

/**
 * Signal hero variant — eyebrow breadcrumb (status / step kind / source) +
 * verb-emphasis title + sub line. Uses the Beacon PageHeader primitive.
 */
export function QueryHero({ query, onExecute, onAddSubscription }: QueryHeroProps) {
  const sourceLabel = query.aiActorName ?? 'USER-DEFINED';
  const stepLabel = query.isMultiStep ? 'multi-step' : 'single-step';
  const dataSourceText = query.dataSourceNames.length > 0
    ? query.dataSourceNames.join(', ')
    : '—';

  return (
    <PageHeader
      variant="signal"
      eyebrow={
        <>
          <Link to="/queries" className="hover:text-text">Queries</Link>
          <span className="eyebrow-sep">/</span>
          <span className="mono normal-case tracking-normal">#{query.id}</span>
          <span className="eyebrow-sep">·</span>
          <Pill tone="ok" dot>ACTIVE</Pill>
          <span className="eyebrow-sep">·</span>
          <Pill className="mono normal-case tracking-normal">{stepLabel.toUpperCase()}</Pill>
          <span className="eyebrow-sep">·</span>
          <Pill dot>{sourceLabel.toUpperCase()}</Pill>
          {query.isLocked && (
            <>
              <span className="eyebrow-sep">·</span>
              <Pill tone="warn"><Lock size={10} /> LOCKED</Pill>
            </>
          )}
        </>
      }
      prefix="Editing"
      emphasis={query.name}
      sub={
        <>
          {stepLabel} query against <span className="mono">{dataSourceText}</span>
          {' · '}created <span className="mono">{formatDateTime(query.createdTime)}</span>
          {' · '}<span className="mono">{query.totalExecutions.toLocaleString()}</span> run{query.totalExecutions === 1 ? '' : 's'} to date
        </>
      }
      actions={
        <>
          {onAddSubscription && (
            <Button icon={<Bell />} onClick={onAddSubscription}>Add subscription</Button>
          )}
          {onExecute && (
            <Button variant="primary" icon={<Zap />} onClick={onExecute}>Execute query</Button>
          )}
        </>
      }
    />
  );
}
