import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import {
  AlertTriangle,
  Check,
  Clock,
  GitBranch,
  Inbox,
  Lightbulb,
  Lock,
  Users,
  Zap,
} from 'lucide-react';
import {
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardBody,
  Kbd,
} from '@/components/beacon';
import { cn } from '@/lib/cn';
import type { QueryDetail } from '../queries';

interface RightRailProps {
  query: QueryDetail;
  editHref: string;
}

export function RightRail({ query, editHref }: RightRailProps) {
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
    <aside className="flex flex-col gap-5">
      <Card>
        <CardHead>
          <Lightbulb className="size-3.5 text-text-muted" />
          <CardTitle>Suggested next steps</CardTitle>
          <CardSub>heuristic</CardSub>
        </CardHead>
        <CardBody className="flex flex-col gap-2">
          {neverRun && (
            <NextStep
              tone="info"
              icon={<Zap size={13} />}
              title="Run for the first time"
              sub="No executions on record. Run once to populate KPIs and timing samples."
            />
          )}
          {noSubscriptions && (
            <NextStep
              tone="warn"
              icon={<Inbox size={13} />}
              title="Add a subscription"
              sub="No subscribers will be notified — schedule this query first."
            />
          )}
          {hasFailures && (
            <NextStep
              tone="warn"
              icon={<AlertTriangle size={13} />}
              title="Review recent failures"
              sub={`${totalRecentFailures} failed execution${totalRecentFailures === 1 ? '' : 's'} in the recent window.`}
            />
          )}
          {showLockNudge && (
            <NextStep
              tone="info"
              icon={<Lock size={13} />}
              title="Lock against AI edits"
              sub={`Managed by ${query.aiActorName}. Lock to prevent future automated changes.`}
            />
          )}
          {!neverRun && !noSubscriptions && !hasFailures && !showLockNudge && (
            <NextStep
              tone="ok"
              icon={<Check size={13} />}
              title="Query is healthy"
              sub="Recent executions succeeded and a subscription is wired."
            />
          )}
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Users className="size-3.5 text-text-muted" />
          <CardTitle>People & sources</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2.5">
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
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <GitBranch className="size-3.5 text-text-muted" />
          <CardTitle>Source context</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2">
          <CheckRow
            tone="ok"
            title={`${query.dataSourceNames.length} data source${query.dataSourceNames.length === 1 ? '' : 's'}`}
            detail={query.dataSourceNames.join(' · ') || '—'}
          />
          <CheckRow
            tone={query.isCrossDatabase ? 'warn' : 'ok'}
            title={query.isCrossDatabase ? 'Cross-database query' : 'Single-database query'}
            detail={query.isCrossDatabase ? 'joins materialize via in-memory SQLite' : 'native execution'}
          />
          <CheckRow
            tone={query.totalExecutions > 0 ? 'ok' : 'pending'}
            title={query.totalExecutions > 0 ? 'Run history present' : 'No runs recorded'}
            detail={`${query.totalExecutions} total execution${query.totalExecutions === 1 ? '' : 's'}`}
          />
        </CardBody>
      </Card>

      <div className="flex items-start gap-3 p-3.5 rounded-md border border-info/30 bg-info-bg">
        <Lightbulb className="size-4 text-info shrink-0 mt-0.5" />
        <div className="text-sm">
          <div className="font-medium">Tip · iteration</div>
          <div className="text-xs text-text-muted mt-0.5">
            Use the{' '}
            <Link to={editHref} className="mono text-brand-600">editor</Link>
            {' '}to modify steps and SQL. Press <Kbd>⌘</Kbd><Kbd>↵</Kbd> to preview results.
          </div>
        </div>
      </div>
    </aside>
  );
}

const toneBg: Record<'warn' | 'ok' | 'info', string> = {
  warn: 'bg-warn-bg border-warn/30',
  ok: 'bg-ok-bg border-ok/30',
  info: 'bg-info-bg border-info/30',
};
const toneIcon: Record<'warn' | 'ok' | 'info', string> = {
  warn: 'text-warn',
  ok: 'text-ok',
  info: 'text-info',
};

function NextStep({
  tone, icon, title, sub,
}: { tone: 'warn' | 'ok' | 'info'; icon: ReactNode; title: string; sub: string }) {
  return (
    <div className={cn('flex items-start gap-2.5 p-2.5 rounded-md border', toneBg[tone])}>
      <span className={cn('shrink-0 mt-0.5', toneIcon[tone])}>{icon}</span>
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium">{title}</div>
        <div className="text-xs text-text-muted mt-0.5">{sub}</div>
      </div>
    </div>
  );
}

function PersonRow({
  role, name, sub, muted,
}: { role: string; name: string; sub: string; muted?: boolean }) {
  return (
    <div className="flex items-center gap-2.5">
      <div
        className={cn(
          'size-7 rounded-full inline-flex items-center justify-center text-2xs font-semibold',
          muted
            ? 'bg-surface-2 text-text-muted border border-border'
            : 'bg-brand-100 text-brand-700 border border-brand-200',
        )}
      >
        {muted ? <Users size={12} /> : initials(name)}
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{role}</div>
        <div className="text-sm font-medium truncate">{name}</div>
        <div className="text-xs text-text-muted truncate">{sub}</div>
      </div>
    </div>
  );
}

const checkTone: Record<'ok' | 'warn' | 'pending', string> = {
  ok: 'bg-ok-bg text-ok',
  warn: 'bg-warn-bg text-warn',
  pending: 'bg-surface-2 text-text-muted',
};

function CheckRow({
  tone, title, detail,
}: { tone: 'ok' | 'warn' | 'pending'; title: string; detail: string }) {
  return (
    <div className="flex items-start gap-2.5">
      <span className={cn('inline-flex items-center justify-center size-5 rounded-full shrink-0 mt-0.5', checkTone[tone])}>
        {tone === 'ok' ? <Check size={11} /> : tone === 'warn' ? <AlertTriangle size={11} /> : <Clock size={11} />}
      </span>
      <div className="flex-1 min-w-0">
        <div className="text-sm">{title}</div>
        <div className="text-xs text-text-muted">{detail}</div>
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
