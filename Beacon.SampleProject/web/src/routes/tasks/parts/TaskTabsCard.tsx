import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
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

  return (
    <div className="card">
      <div className="tabs">
        <Tab active={tab === 'activity'} onClick={() => onTabChange('activity')}>
          <Icon.Activity size={13} /> Activity <span className="tab__count">{activity.length}</span>
        </Tab>
        <Tab active={tab === 'history'} onClick={() => onTabChange('history')}>
          <Icon.Clock size={13} /> Executions <span className="tab__count">{executions.length}</span>
        </Tab>
        <Tab active={tab === 'notif'} onClick={() => onTabChange('notif')}>
          <Icon.Bell size={13} /> Notifications <span className="tab__count">{task.notificationCount}</span>
        </Tab>
        <Tab active={tab === 'related'} onClick={() => onTabChange('related')}>
          <Icon.Branch size={13} /> Related <span className="tab__count">{related.length}</span>
        </Tab>
        {onRefresh && (
          <div style={{ marginLeft: 'auto', padding: '6px 16px 6px 0', display: 'flex', gap: 6 }}>
            <button type="button" className="btn btn--ghost" onClick={onRefresh}>
              <Icon.Refresh size={13} className="btn__icon" /> Refresh
            </button>
          </div>
        )}
      </div>

      {tab === 'activity' && (
        <div className="card__body" style={{ paddingTop: 18 }}>
          {activity.length === 0
            ? <span className="muted">No activity yet.</span>
            : (
              <div className="timeline">
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
        </div>
      )}

      {tab === 'history' && (
        <div className="data-tbl">
          {executions.length === 0
            ? <div className="card__body"><span className="muted">No executions recorded.</span></div>
            : (
              <table>
                <thead>
                  <tr>
                    <th>Execution</th>
                    <th>Executed at</th>
                    <th style={{ textAlign: 'right' }}>Duration</th>
                    <th style={{ textAlign: 'right' }}>Rows</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {executions.map(x => (
                    <tr key={x.id}>
                      <td><span className="mono" style={{ color: 'var(--brand-600)' }}>#{x.id}</span></td>
                      <td className="mono">{formatDateTime(x.executedAt)}</td>
                      <td className="mono" style={{ textAlign: 'right' }}>{x.durationMs.toFixed(2)} ms</td>
                      <td className="mono" style={{ textAlign: 'right' }}>{x.rowCount}</td>
                      <td>
                        <span className={`pill ${x.rowCount > 0 ? 'pill--ok' : 'pill--neutral'}`}>
                          <span className="pill__dot" />{x.status}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </div>
      )}

      {tab === 'notif' && (
        <div className="card__body">
          <div className="empty-state">
            <div className="empty-state__icon"><Icon.Bell size={20} /></div>
            <div>
              <div className="empty-state__title">
                {task.notificationCount === 0
                  ? 'No notifications delivered yet'
                  : `${task.notificationCount} notification${task.notificationCount === 1 ? '' : 's'} delivered`}
              </div>
              <div className="empty-state__sub">
                {task.notificationCount === 0
                  ? 'Wire recipients on the subscription to start sending email, Slack, or webhook notifications.'
                  : task.lastNotificationAt
                    ? `Last delivered ${formatRelativeTime(task.lastNotificationAt)}.`
                    : 'Notifications recorded for this task.'}
              </div>
            </div>
          </div>
        </div>
      )}

      {tab === 'related' && (
        <div className="data-tbl">
          {related.length === 0
            ? <div className="card__body"><span className="muted">No related tasks.</span></div>
            : (
              <table>
                <thead>
                  <tr>
                    <th>Task</th>
                    <th>Opened</th>
                    <th style={{ textAlign: 'right' }}>Last result</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {related.map(r => (
                    <tr key={r.id}>
                      <td><Link to={`/tasks/${r.id}`} className="mono" style={{ color: 'var(--brand-600)' }}>#{r.id}</Link></td>
                      <td className="mono subtle">{formatDateTime(r.createdAt)}</td>
                      <td className="mono" style={{ textAlign: 'right' }}>{r.latestResultCount}</td>
                      <td>
                        {r.resolved
                          ? <span className="pill pill--ok"><span className="pill__dot" />resolved</span>
                          : <span className="pill pill--warn"><span className="pill__dot" />open</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </div>
      )}
    </div>
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

function Tab({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" className={`tab${active ? ' active' : ''}`} onClick={onClick}>
      {children}
    </button>
  );
}

function TLItem({
  time, tone, title, sub, last,
}: { time: string; tone: string; title: ReactNode; sub: ReactNode; last?: boolean }) {
  return (
    <div className={`tl${last ? ' tl--last' : ''}`}>
      <div className="tl__rail">
        <span className={`tl__dot tl__dot--${tone}`} />
      </div>
      <div className="tl__body">
        <div className="tl__title">{title}</div>
        <div className="tl__sub">{sub}</div>
      </div>
      <div className="tl__time mono">{time}</div>
    </div>
  );
}

export type { TabKey };
