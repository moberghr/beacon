import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { formatDateTime } from '@/lib/format';
import type { TaskDetail } from '../queries';

export function TaskInfoCard({ task }: { task: TaskDetail }) {
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
            label="Subscription"
            value={<span className="mono">{task.subscriptionName}</span>}
          />
          <KV
            label="Query"
            value={
              <Link to={`/queries/${task.queryId}/versions`} className="mono" style={{ color: 'var(--brand-600)' }}>
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
            label="Owner"
            value={task.aiActorName
              ? <span className="mono">{task.aiActorName} · ai actor</span>
              : <span className="subtle">user</span>}
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
