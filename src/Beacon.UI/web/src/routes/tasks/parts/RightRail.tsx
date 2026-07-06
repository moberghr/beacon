import type { ReactNode } from 'react';
import {
  AlertTriangle,
  Bell,
  Check,
  Clock,
  GitBranch,
  Lightbulb,
  Users,
} from 'lucide-react';
import {
  Banner,
  Button,
  Card,
  CardBody,
  CardHead,
  CardTitle,
  Kbd,
} from '@/components/beacon';
import { cn } from '@/lib/cn';
import { useUnwatchTask, useWatchTask, type TaskDetail } from '../queries';

interface RightRailProps {
  task: TaskDetail;
  relatedResolvedCount: number;
}

export function RightRail({ task, relatedResolvedCount }: RightRailProps) {
  const noRecipients = task.notificationCount === 0;
  const watch = useWatchTask(task.id);
  const unwatch = useUnwatchTask(task.id);

  const watchBusy = watch.isPending || unwatch.isPending;
  const onToggleWatch = () => {
    if (watchBusy) return;
    if (task.isWatching) unwatch.mutate();
    else watch.mutate();
  };

  return (
    <aside className="flex flex-col gap-5">
      <Card>
        <CardHead>
          <Lightbulb className="size-3.5 text-text-muted" />
          <CardTitle>Suggested next steps</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2">
          {!task.resolved && noRecipients && (
            <NextStep
              tone="warn"
              icon={<Bell className="size-3.5" />}
              title="Wire a recipient"
              sub={`Notifications have not been delivered — no recipients on subscription ${task.subscriptionName}.`}
            />
          )}
          {!task.resolved && !task.assigneeUserId && (
            <NextStep
              tone="info"
              icon={<Users className="size-3.5" />}
              title="Claim ownership"
              sub="Task is unassigned. Assigning yourself stops further auto-pings."
            />
          )}
          {!task.resolved && relatedResolvedCount >= 2 && (
            <NextStep
              tone="ok"
              icon={<Check className="size-3.5" />}
              title="Mark as expected"
              sub={`${relatedResolvedCount} prior tasks from this subscription auto-closed.`}
            />
          )}
          {task.resolved && (
            <NextStep
              tone="ok"
              icon={<Check className="size-3.5" />}
              title="Subscription healthy"
              sub="No further action required."
            />
          )}
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Users className="size-3.5 text-text-muted" />
          <CardTitle>People</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2.5">
          <PersonRow
            role="Assignee"
            name={task.assigneeUserName ?? task.assigneeUserId ?? 'Unassigned'}
            sub={task.assigneeUserId ? 'investigating' : 'no one assigned'}
            muted={!task.assigneeUserId}
          />
          <PersonRow
            role="Owner"
            name={task.ownerUserName ?? task.ownerUserId ?? '—'}
            sub={task.ownerUserId ? 'created the source' : 'unknown'}
            muted={!task.ownerUserId}
          />
          <PersonRow
            role="Resolved by"
            name={task.resolvedByUserName ?? 'Not resolved'}
            sub={task.resolvedByUserName ? 'closed this task' : 'still open'}
            muted={!task.resolvedByUserName}
          />
          <div className="flex items-center gap-2.5">
            <div className="shrink-0 size-7 grid place-items-center rounded-sm bg-surface-2 text-text-muted">
              <Bell className="size-3" />
            </div>
            <div className="flex-1 min-w-0">
              <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Watchers</div>
              <div className="text-sm font-medium">{task.watcherCount} watching</div>
            </div>
            <Button
              variant={task.isWatching ? 'secondary' : 'primary'}
              size="sm"
              onClick={onToggleWatch}
              disabled={watchBusy}
            >
              {task.isWatching ? 'Watching' : 'Watch'}
            </Button>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <GitBranch className="size-3.5 text-text-muted" />
          <CardTitle>Source context</CardTitle>
        </CardHead>
        <CardBody className="flex flex-col gap-2">
          <CheckRow
            tone={task.cronExpression ? 'ok' : 'pending'}
            title={task.cronExpression ? 'Subscription healthy' : 'Subscription cron unknown'}
            detail={task.cronExpression ? `${task.subscriptionName} · cron ${task.cronExpression}` : task.subscriptionName}
          />
          <CheckRow
            tone={task.lastExecutionAt ? 'ok' : 'pending'}
            title={task.lastExecutionAt ? 'Last execution recorded' : 'No executions recorded'}
            detail={task.lastExecutionAt ?? '—'}
          />
          <CheckRow
            tone={noRecipients ? 'warn' : 'ok'}
            title={noRecipients ? 'No notifications sent' : `${task.notificationCount} notification(s) sent`}
            detail={noRecipients ? 'check recipients on subscription' : 'recipients are wired'}
          />
        </CardBody>
      </Card>

      <Banner
        tone="info"
        icon={<Lightbulb />}
        title="Tip · keyboard shortcuts"
        sub={
          <>
            <Kbd>R</Kbd> resolve · <Kbd>A</Kbd> assign-to-me ·{' '}
            <Kbd>S</Kbd> snooze 1h · <Kbd>C</Kbd> comment
          </>
        }
      />
    </aside>
  );
}

const toneBorder = {
  warn: 'border-warn/30 bg-warn-bg',
  ok: 'border-ok/30 bg-ok-bg',
  info: 'border-info/30 bg-info-bg',
};

function NextStep({
  tone, icon, title, sub,
}: { tone: 'warn' | 'ok' | 'info'; icon: ReactNode; title: string; sub: string }) {
  return (
    <div className={cn('flex items-start gap-2.5 p-2.5 rounded-sm border', toneBorder[tone])}>
      <span className="shrink-0 mt-0.5 text-text-muted">{icon}</span>
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
