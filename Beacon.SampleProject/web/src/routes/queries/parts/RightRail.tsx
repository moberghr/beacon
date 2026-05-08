import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
import type { QueryDetail } from '../queries';

interface RightRailProps {
  query: QueryDetail;
  legacyEditHref: string;
}

export function RightRail({ query, legacyEditHref }: RightRailProps) {
  const noSubscriptions = query.subscriptions.length === 0;
  const isAiManaged = query.aiActorId != null;
  const totalRecentFailures = query.notificationHistory.reduce(
    (sum, day) => sum + day.failedExecutions,
    0,
  );
  const hasFailures = totalRecentFailures > 0;
  const neverRun = query.totalExecutions === 0;
  const showLockNudge = isAiManaged && !query.isLocked;

  return (
    <aside className="q-aside">
      <div className="card">
        <div className="card__head">
          <Icon.Lightbulb size={15} className="muted" />
          <h3 className="card__title">Suggested next steps</h3>
          <span className="card__sub">heuristic</span>
        </div>
        <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {neverRun && (
            <NextStep
              tone="info"
              icon={<Icon.Bolt size={13} />}
              title="Run for the first time"
              sub="No executions on record. Run once to populate KPIs and timing samples."
            />
          )}
          {noSubscriptions && (
            <NextStep
              tone="warn"
              icon={<Icon.Inbox size={13} />}
              title="Add a subscription"
              sub="No subscribers will be notified — schedule this query first."
            />
          )}
          {hasFailures && (
            <NextStep
              tone="warn"
              icon={<Icon.Alert size={13} />}
              title="Review recent failures"
              sub={`${totalRecentFailures} failed execution${totalRecentFailures === 1 ? '' : 's'} in the recent window.`}
            />
          )}
          {showLockNudge && (
            <NextStep
              tone="info"
              icon={<Icon.Lock size={13} />}
              title="Lock against AI edits"
              sub={`Managed by ${query.aiActorName}. Lock to prevent future automated changes.`}
            />
          )}
          {!neverRun && !noSubscriptions && !hasFailures && !showLockNudge && (
            <NextStep
              tone="ok"
              icon={<Icon.Check size={13} />}
              title="Query is healthy"
              sub="Recent executions succeeded and a subscription is wired."
            />
          )}
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Users size={15} className="muted" />
          <h3 className="card__title">People & sources</h3>
        </div>
        <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <PersonRow
            role="Owner"
            name={query.aiActorName ?? 'User-defined'}
            sub={query.aiActorName ? 'AI actor — managed' : 'manual'}
            muted={!query.aiActorName}
          />
          <PersonRow
            role="Subscriptions"
            name={`${query.subscriptions.length} active`}
            sub={query.subscriptions[0]?.name ?? 'none configured'}
            muted={query.subscriptions.length === 0}
          />
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Branch size={15} className="muted" />
          <h3 className="card__title">Source context</h3>
        </div>
        <div className="checks">
          <Check
            tone="ok"
            title={`${query.dataSourceNames.length} data source${query.dataSourceNames.length === 1 ? '' : 's'}`}
            detail={query.dataSourceNames.join(' · ') || '—'}
          />
          <Check
            tone={query.isCrossDatabase ? 'warn' : 'ok'}
            title={query.isCrossDatabase ? 'Cross-database query' : 'Single-database query'}
            detail={query.isCrossDatabase ? 'joins materialize via in-memory SQLite' : 'native execution'}
          />
          <Check
            tone={query.totalExecutions > 0 ? 'ok' : 'pending'}
            title={query.totalExecutions > 0 ? 'Run history present' : 'No runs recorded'}
            detail={`${query.totalExecutions} total execution${query.totalExecutions === 1 ? '' : 's'}`}
          />
        </div>
      </div>

      <div className="callout">
        <Icon.Lightbulb size={16} className="callout__icon" />
        <div>
          <div className="callout__title">Tip · iteration</div>
          <div className="callout__sub">
            The legacy editor at <a href={legacyEditHref} className="mono" style={{ color: 'var(--brand-600)' }}>edit</a>{' '}
            still owns step CRUD and inline execution until Phase 5f.
          </div>
        </div>
      </div>
    </aside>
  );
}

function NextStep({
  tone, icon, title, sub,
}: { tone: 'warn' | 'ok' | 'info'; icon: ReactNode; title: string; sub: string }) {
  return (
    <div className={`next-step next-step--${tone}`}>
      <span className="next-step__icon">{icon}</span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="next-step__title">{title}</div>
        <div className="next-step__sub">{sub}</div>
      </div>
    </div>
  );
}

function PersonRow({
  role, name, sub, muted,
}: { role: string; name: string; sub: string; muted?: boolean }) {
  return (
    <div className="person-row">
      <div className={`avatar avatar--sm${muted ? ' avatar--muted' : ''}`}>
        {muted ? <Icon.Users size={12} /> : initials(name)}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="person-row__role">{role}</div>
        <div className="person-row__name">{name}</div>
        <div className="person-row__sub">{sub}</div>
      </div>
    </div>
  );
}

function Check({
  tone, title, detail,
}: { tone: 'ok' | 'warn' | 'pending'; title: string; detail: string }) {
  return (
    <div className="check">
      <span className={`check__icon check__icon--${tone}`}>
        {tone === 'ok' ? <Icon.Check size={11} /> : tone === 'warn' ? <Icon.Alert size={11} /> : <Icon.Clock size={11} />}
      </span>
      <div className="check__main">
        <div>{title}</div>
        <div className="check__detail">{detail}</div>
      </div>
    </div>
  );
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  const first = parts[0]?.[0] ?? '';
  const second = parts[1]?.[0] ?? '';
  return (first + second).toUpperCase() || '·';
}
