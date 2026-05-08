import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { useAuth } from '@/auth/useAuth';
import { formatDateTime } from '@/lib/format';
import {
  useAssignTask,
  useSetTaskPriority,
  type TaskDetail,
  type TaskPriority,
} from '../queries';

const PRIORITY_OPTIONS: { value: TaskPriority; label: string }[] = [
  { value: 1, label: 'P1 — Critical' },
  { value: 2, label: 'P2 — High' },
  { value: 3, label: 'P3 — Normal' },
  { value: 4, label: 'P4 — Low' },
];

export function TaskInfoCard({ task }: { task: TaskDetail }) {
  const { data: currentUser } = useAuth();
  const assign = useAssignTask(task.id);
  const setPriority = useSetTaskPriority(task.id);

  const onClaim = () => {
    if (!currentUser?.userId) return;
    assign.mutate({ assigneeUserId: currentUser.userId });
  };

  const assigneeText =
    task.assigneeUserName ?? task.assigneeUserId ?? null;

  return (
    <div className="card">
      <div className="card__head">
        <Icon.Info size={15} className="muted" />
        <h3 className="card__title">Task information</h3>
      </div>
      <div className="card__body">
        <div className="kv">
          <KV label="Task ID" value={<span className="mono">#{task.id}</span>} />
          <KV
            label="Status"
            value={task.resolved
              ? <span className="pill pill--ok"><span className="pill__dot" />resolved</span>
              : <span className="pill pill--warn"><span className="pill__dot" />open</span>}
          />
          <KV
            label="Priority"
            value={
              <select
                className="q-input"
                style={{ fontSize: 12.5, padding: '2px 6px' }}
                value={task.priority}
                disabled={setPriority.isPending || task.resolved}
                onChange={e => {
                  const next = Number(e.target.value) as TaskPriority;
                  if (next !== task.priority) setPriority.mutate({ priority: next });
                }}
              >
                {PRIORITY_OPTIONS.map(opt => (
                  <option key={opt.value} value={opt.value}>{opt.label}</option>
                ))}
              </select>
            }
          />
          <KV
            label="Assignee"
            value={assigneeText
              ? <span className="mono">{assigneeText}</span>
              : (
                <span>
                  <span className="subtle">unassigned</span>
                  {!task.resolved && currentUser?.userId && (
                    <button
                      type="button"
                      className="btn btn--ghost"
                      onClick={onClaim}
                      disabled={assign.isPending}
                      style={{ marginLeft: 8, padding: '2px 6px', fontSize: 11.5 }}
                    >
                      claim
                    </button>
                  )}
                </span>
              )}
          />
          <KV
            label="Subscription"
            value={<span className="mono">{task.subscriptionName}</span>}
          />
          <KV
            label="Query"
            value={
              <Link to={`/queries/${task.queryId}`} className="mono" style={{ color: 'var(--brand-600)' }}>
                {task.queryName}
              </Link>
            }
          />
          <KV
            label="Created"
            value={<span className="mono">{formatDateTime(task.createdAt)}</span>}
          />
          <KV
            label="Last execution"
            value={task.lastExecutionAt
              ? <span className="mono">{formatDateTime(task.lastExecutionAt)}</span>
              : <span className="subtle">never</span>}
          />
          <KV
            label="Triggered by"
            value={task.cronExpression
              ? <span className="mono">cron · {task.cronExpression}</span>
              : <span className="subtle">manual</span>}
          />
          <KV
            label="Snoozed until"
            value={task.snoozedUntil
              ? <span className="mono">{formatDateTime(task.snoozedUntil)}</span>
              : <span className="subtle">—</span>}
          />
          <KV
            label="Resolved by"
            value={task.resolvedByUserName
              ? <span className="mono">{task.resolvedByUserName}</span>
              : <span className="subtle">—</span>}
          />
          <KV
            label="Notifications"
            value={<span className="mono">{task.notificationCount}</span>}
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
