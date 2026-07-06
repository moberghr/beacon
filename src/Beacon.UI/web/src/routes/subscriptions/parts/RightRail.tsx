import type { ReactNode } from 'react';
import {
  AlertTriangle,
  Check,
  ChevronRight,
  Clock,
  Lightbulb,
  Users,
  Zap,
} from 'lucide-react';
import {
  Card,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
} from '@/components/beacon';
import { cn } from '@/lib/cn';
import type {
  SubscriptionDetail,
  SubscriptionExecutionEntry,
} from '../queries';
import { NOTIFICATION_STATUS_LABEL } from '../queries';
import { NotificationStatus } from '@/lib/enums';
import type { SubscriptionTabKey } from './SubscriptionTabsCard';

interface RightRailProps {
  subscription: SubscriptionDetail;
  executions: SubscriptionExecutionEntry[] | undefined;
  onSelectTab?: (tab: SubscriptionTabKey) => void;
}

export function RightRail({ subscription, executions, onSelectTab }: RightRailProps) {
  const list = executions ?? [];
  const totalExecs = list.length;
  const failed = list.filter(x => x.status === NotificationStatus.Timeout || x.status === NotificationStatus.Failed).length;
  const noRecipients = subscription.recipients.length === 0;
  const neverRun = totalExecs === 0;
  const anomalyOff = subscription.anomalyConfig?.enabled !== true;
  const isAi = subscription.aiActorId != null;

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
              icon={<Zap className="size-3.5" />}
              title="Run once to validate"
              sub="No executions on record. Use Test now to verify the schedule and recipients."
            />
          )}
          {noRecipients && (
            <NextStep
              tone="warn"
              icon={<Users className="size-3.5" />}
              title="Add a recipient"
              sub={
                subscription.createTasks
                  ? 'Tasks will still be created, but no one will be notified directly.'
                  : 'Without recipients, this subscription will not deliver anywhere.'
              }
              onClick={onSelectTab ? () => onSelectTab('recipients') : undefined}
            />
          )}
          {failed > 0 && (
            <NextStep
              tone="warn"
              icon={<AlertTriangle className="size-3.5" />}
              title="Review recent failures"
              sub={`${failed} failed/timed-out execution${failed === 1 ? '' : 's'} in the recent window.`}
              onClick={onSelectTab ? () => onSelectTab('executions') : undefined}
            />
          )}
          {!neverRun && !noRecipients && failed === 0 && !anomalyOff && (
            <NextStep
              tone="ok"
              icon={<Check className="size-3.5" />}
              title="Subscription is healthy"
              sub="Recent executions succeeded and detection is wired."
            />
          )}
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Users className="size-3.5 text-text-muted" />
          <CardTitle>Owner &amp; query</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2.5">
          <PersonRow
            role="Owner"
            name={subscription.aiActorName ?? 'User-defined'}
            sub={isAi ? 'AI actor — managed' : 'manual'}
            muted={!isAi}
          />
          <PersonRow
            role="Query"
            name={subscription.queryName}
            sub={`#${subscription.queryId}`}
          />
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Clock className="size-3.5 text-text-muted" />
          <CardTitle>Latest run</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2">
          {list.length === 0 ? (
            <span className="text-text-muted">No runs recorded yet.</span>
          ) : (
            (() => {
              const latest = list
                .slice()
                .sort((a, b) => +new Date(b.createdTime) - +new Date(a.createdTime))[0];
              return (
                <>
                  <CheckRow
                    tone={latest.status === NotificationStatus.NotificationSent ? 'ok' : latest.status === NotificationStatus.Timeout || latest.status === NotificationStatus.Failed ? 'warn' : 'pending'}
                    title={NOTIFICATION_STATUS_LABEL[latest.status] ?? 'unknown'}
                    detail={`${latest.resultCount.toLocaleString()} result${latest.resultCount === 1 ? '' : 's'} · ${Math.round(latest.executionTimeMs)} ms`}
                  />
                  <CheckRow
                    tone={latest.recipientNames.length > 0 ? 'ok' : 'pending'}
                    title={`${latest.recipientNames.length} recipient${latest.recipientNames.length === 1 ? '' : 's'} notified`}
                    detail={latest.recipientNames.slice(0, 2).join(', ') || '—'}
                  />
                </>
              );
            })()
          )}
        </CardBody>
      </Card>
    </aside>
  );
}

const toneBorder = {
  warn: 'border-warn/30 bg-warn-bg',
  ok: 'border-ok/30 bg-ok-bg',
  info: 'border-info/30 bg-info-bg',
};

function NextStep({
  tone, icon, title, sub, onClick,
}: { tone: 'warn' | 'ok' | 'info'; icon: ReactNode; title: string; sub: string; onClick?: () => void }) {
  const content = (
    <>
      <span className="shrink-0 mt-0.5 text-text-muted [&>svg]:size-3.5">{icon}</span>
      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium">{title}</div>
        <div className="text-xs text-text-muted mt-0.5">{sub}</div>
      </div>
      {onClick && <ChevronRight className="size-3.5 text-text-muted" />}
    </>
  );
  const cls = cn(
    'flex items-start gap-2.5 p-2.5 rounded-sm border text-left w-full',
    toneBorder[tone],
  );
  if (onClick) {
    return (
      <button type="button" className={cn(cls, 'hover:brightness-95 transition')} onClick={onClick}>
        {content}
      </button>
    );
  }
  return <div className={cls}>{content}</div>;
}

function PersonRow({
  role, name, sub, muted,
}: { role: string; name: string; sub: string; muted?: boolean }) {
  return (
    <div className="flex items-center gap-2.5">
      <div
        className={cn(
          'shrink-0 size-7 grid place-items-center rounded-sm text-2xs font-semibold uppercase tracking-eyebrow',
          muted ? 'bg-surface-2 text-text-muted' : 'bg-brand-100 text-brand-700',
        )}
      >
        {muted ? <Users className="size-3" /> : initials(name)}
      </div>
      <div className="flex-1 min-w-0">
        <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{role}</div>
        <div className="text-sm font-medium truncate">{name}</div>
        <div className="text-xs text-text-muted truncate">{sub}</div>
      </div>
    </div>
  );
}

function CheckRow({
  tone, title, detail,
}: { tone: 'ok' | 'warn' | 'pending'; title: string; detail: string }) {
  const iconCls = {
    ok: 'text-ok',
    warn: 'text-warn',
    pending: 'text-text-muted',
  }[tone];
  return (
    <div className="flex items-start gap-2.5">
      <span className={cn('shrink-0 mt-0.5 [&>svg]:size-3', iconCls)}>
        {tone === 'ok' ? <Check /> : tone === 'warn' ? <AlertTriangle /> : <Clock />}
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
