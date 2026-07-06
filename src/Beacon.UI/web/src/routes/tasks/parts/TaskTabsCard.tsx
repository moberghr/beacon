import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import {
  Activity,
  Bell,
  Clock,
  GitBranch,
  RefreshCw,
} from 'lucide-react';
import {
  Button,
  Card,
  CardBody,
  Pill,
} from '@/components/beacon';
import { Tabs, type TabDef } from '@/components/Tabs';
import { cn } from '@/lib/cn';
import { formatDateTime, formatRelativeTime } from '@/lib/format';
import type {
  TaskCommentItem,
  TaskDetail,
  TaskExecutionItem,
  TaskRelatedItem,
} from '../queries';

type TabKey = 'activity' | 'history' | 'notif' | 'related';

interface TaskTabsCardProps {
  tab: TabKey;
  onTabChange: (k: TabKey) => void;
  task: TaskDetail;
  executions: TaskExecutionItem[];
  comments: TaskCommentItem[];
  related: TaskRelatedItem[];
  onRefresh?: () => void;
}

export function TaskTabsCard({
  tab,
  onTabChange,
  task,
  executions,
  comments,
  related,
  onRefresh,
}: TaskTabsCardProps) {
  const activity = useMemo(
    () => buildActivity(task, executions, comments),
    [task, executions, comments],
  );

  const tabs: TabDef<TabKey>[] = [
    {
      key: 'activity',
      label: <span className="inline-flex items-center gap-1.5"><Activity className="size-3.5" /> Activity</span>,
      count: activity.length,
    },
    {
      key: 'history',
      label: <span className="inline-flex items-center gap-1.5"><Clock className="size-3.5" /> Executions</span>,
      count: executions.length,
    },
    {
      key: 'notif',
      label: <span className="inline-flex items-center gap-1.5"><Bell className="size-3.5" /> Notifications</span>,
      count: task.notificationCount,
    },
    {
      key: 'related',
      label: <span className="inline-flex items-center gap-1.5"><GitBranch className="size-3.5" /> Related</span>,
      count: related.length,
    },
  ];

  return (
    <Card>
      <Tabs
        tabs={tabs}
        active={tab}
        onChange={onTabChange}
        trailing={
          onRefresh && (
            <Button variant="ghost" size="sm" icon={<RefreshCw />} onClick={onRefresh}>
              Refresh
            </Button>
          )
        }
      />

      {tab === 'activity' && (
        <CardBody>
          {activity.length === 0
            ? <span className="text-text-muted">No activity yet.</span>
            : (
              <div className="py-1">
                {activity.map((item, i) => (
                  <TLItem
                    key={item.key}
                    time={item.time}
                    tone={item.tone}
                    title={item.title}
                    sub={item.sub}
                    last={i === activity.length - 1}
                  />
                ))}
              </div>
            )}
        </CardBody>
      )}

      {tab === 'history' && (
        <CardBody flush>
          {executions.length === 0
            ? <div className="p-4"><span className="text-text-muted">No executions recorded.</span></div>
            : (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse text-xs">
                  <thead>
                    <tr>
                      <Th>Execution</Th>
                      <Th>Executed at</Th>
                      <Th align="right">Duration</Th>
                      <Th align="right">Rows</Th>
                      <Th>Status</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {executions.map(x => (
                      <tr key={x.id} className="hover:bg-surface-2">
                        <Td><span className="mono text-brand-600">#{x.id}</span></Td>
                        <Td className="mono">{formatDateTime(x.executedAt)}</Td>
                        <Td className="mono text-right">{x.durationMs.toFixed(2)} ms</Td>
                        <Td className="mono text-right">{x.rowCount}</Td>
                        <Td>
                          <Pill tone={x.rowCount > 0 ? 'ok' : 'neutral'} dot>{x.status}</Pill>
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
        </CardBody>
      )}

      {tab === 'notif' && (
        <CardBody>
          <div className="flex items-start gap-3 p-5 rounded-md border border-dashed border-border bg-surface-2">
            <div className="shrink-0 size-9 grid place-items-center rounded-sm bg-surface text-text-muted [&>svg]:size-4">
              <Bell />
            </div>
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-text">
                {task.notificationCount === 0
                  ? 'No notifications delivered yet'
                  : `${task.notificationCount} notification${task.notificationCount === 1 ? '' : 's'} delivered`}
              </div>
              <div className="text-xs text-text-muted mt-0.5">
                {task.notificationCount === 0
                  ? 'Wire recipients on the subscription to start sending email, Slack, or webhook notifications.'
                  : task.lastNotificationAt
                    ? `Last delivered ${formatRelativeTime(task.lastNotificationAt)}.`
                    : 'Notifications recorded for this task.'}
              </div>
            </div>
          </div>
        </CardBody>
      )}

      {tab === 'related' && (
        <CardBody flush>
          {related.length === 0
            ? <div className="p-4"><span className="text-text-muted">No related tasks.</span></div>
            : (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse text-xs">
                  <thead>
                    <tr>
                      <Th>Task</Th>
                      <Th>Opened</Th>
                      <Th align="right">Last result</Th>
                      <Th>Status</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {related.map(r => (
                      <tr key={r.id} className="hover:bg-surface-2">
                        <Td>
                          <Link to={`/tasks/${r.id}`} className="mono text-brand-600">#{r.id}</Link>
                        </Td>
                        <Td className="mono text-text-subtle">{formatDateTime(r.createdAt)}</Td>
                        <Td className="mono text-right">{r.latestResultCount}</Td>
                        <Td>
                          {r.resolved
                            ? <Pill tone="ok" dot>resolved</Pill>
                            : <Pill tone="warn" dot>open</Pill>}
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
        </CardBody>
      )}
    </Card>
  );
}

interface ActivityEntry {
  key: string;
  time: string;
  tone: 'ok' | 'info' | 'warn' | 'err' | 'neutral';
  title: ReactNode;
  sub: ReactNode;
}

/**
 * Synthesizes an activity timeline by merging:
 * - task creation
 * - each execution (with notification-status tone)
 * - each user comment
 * Sorted newest-first.
 */
function buildActivity(
  task: TaskDetail,
  executions: TaskExecutionItem[],
  comments: TaskCommentItem[],
): ActivityEntry[] {
  const entries: Array<ActivityEntry & { ts: number }> = [];

  entries.push({
    key: `task-${task.id}-created`,
    ts: new Date(task.createdAt).getTime(),
    time: formatDateTime(task.createdAt),
    tone: 'info',
    title: `Task #${task.id} opened`,
    sub: <>by subscription <span className="mono">{task.subscriptionName}</span></>,
  });

  if (task.resolved && task.resolvedAt) {
    entries.push({
      key: `task-${task.id}-resolved`,
      ts: new Date(task.resolvedAt).getTime(),
      time: formatDateTime(task.resolvedAt),
      tone: 'ok',
      title: 'Task resolved',
      sub: task.resolvedByUserName
        ? <>by <span className="mono">{task.resolvedByUserName}</span></>
        : 'auto-resolved',
    });
  }

  for (const x of executions) {
    const isAlert = x.rowCount > 0;
    entries.push({
      key: `exec-${x.id}`,
      ts: new Date(x.executedAt).getTime(),
      time: formatDateTime(x.executedAt),
      tone: isAlert ? 'ok' : 'neutral',
      title: `Execution #${x.id} returned ${x.rowCount} row${x.rowCount === 1 ? '' : 's'}`,
      sub: `${x.durationMs.toFixed(2)} ms · ${x.status}`,
    });
  }

  for (const c of comments) {
    entries.push({
      key: `comment-${c.id}`,
      ts: new Date(c.createdAt).getTime(),
      time: formatDateTime(c.createdAt),
      tone: 'info',
      title: <>Note from <span className="mono">{c.userName ?? 'system'}</span></>,
      sub: c.content.length > 80 ? `${c.content.slice(0, 80)}…` : c.content,
    });
  }

  return entries
    .sort((a, b) => b.ts - a.ts)
    .map(({ ts: _ts, ...rest }) => rest);
}

function Th({ children, align }: { children: ReactNode; align?: 'right' }) {
  return (
    <th className={cn(
      'px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-2xs text-text-muted bg-surface-2 border-b border-border whitespace-nowrap',
      align === 'right' ? 'text-right' : 'text-left',
    )}>
      {children}
    </th>
  );
}

function Td({ children, className }: { children: ReactNode; className?: string }) {
  return <td className={cn('px-3.5 py-2.5 border-b border-border', className)}>{children}</td>;
}

function TLItem({
  time, tone, title, sub, last,
}: { time: string; tone: ActivityEntry['tone']; title: ReactNode; sub: ReactNode; last?: boolean }) {
  const dotClass = {
    ok: 'bg-ok',
    info: 'bg-info',
    warn: 'bg-warn',
    err: 'bg-crit',
    neutral: 'bg-text-subtle',
  }[tone];
  return (
    <div className="grid grid-cols-[28px_1fr_auto] gap-2 px-4 pb-3 items-start">
      <div className="relative h-full pt-1 flex justify-center">
        {!last && <span className="absolute top-3.5 -bottom-3 left-1/2 w-px bg-border" />}
        <span
          className={cn(
            'size-2.5 rounded-full relative z-10 ring-2 ring-surface',
            dotClass,
          )}
        />
      </div>
      <div>
        <div className="text-sm font-medium">{title}</div>
        {sub && <div className="text-xs text-text-muted mt-0.5">{sub}</div>}
      </div>
      <div className="text-2xs text-text-subtle mono whitespace-nowrap pt-0.5">{time}</div>
    </div>
  );
}

export type { TabKey };
