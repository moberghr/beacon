import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Info } from 'lucide-react';
import {
  Button,
  Card,
  CardBody,
  CardHead,
  CardTitle,
  Pill,
  Select,
} from '@/components/beacon';
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
    <Card>
      <CardHead>
        <Info className="size-3.5 text-text-muted" />
        <CardTitle>Task information</CardTitle>
      </CardHead>
      <CardBody>
        <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
          <KV label="Task ID" value={<span className="mono">#{task.id}</span>} />
          <KV
            label="Status"
            value={task.resolved
              ? <Pill tone="ok" dot>resolved</Pill>
              : <Pill tone="warn" dot>open</Pill>}
          />
          <KV
            label="Priority"
            value={
              <Select
                className="text-xs py-0.5 px-1.5"
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
              </Select>
            }
          />
          <KV
            label="Assignee"
            value={assigneeText
              ? <span className="mono">{assigneeText}</span>
              : (
                <span className="inline-flex items-center gap-2">
                  <span className="text-text-subtle">unassigned</span>
                  {!task.resolved && currentUser?.userId && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={onClaim}
                      disabled={assign.isPending}
                    >
                      claim
                    </Button>
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
              <Link to={`/queries/${task.queryId}`} className="mono text-brand-600">
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
              : <span className="text-text-subtle">never</span>}
          />
          <KV
            label="Triggered by"
            value={task.cronExpression
              ? <span className="mono">cron · {task.cronExpression}</span>
              : <span className="text-text-subtle">manual</span>}
          />
          <KV
            label="Snoozed until"
            value={task.snoozedUntil
              ? <span className="mono">{formatDateTime(task.snoozedUntil)}</span>
              : <span className="text-text-subtle">—</span>}
          />
          <KV
            label="Resolved by"
            value={task.resolvedByUserName
              ? <span className="mono">{task.resolvedByUserName}</span>
              : <span className="text-text-subtle">—</span>}
          />
          <KV
            label="Notifications"
            value={<span className="mono">{task.notificationCount}</span>}
          />
        </dl>
      </CardBody>
    </Card>
  );
}

function KV({ label, value }: { label: ReactNode; value: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1 border-b border-dashed border-border last:border-b-0">
      <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{label}</dt>
      <dd className="text-sm text-right">{value}</dd>
    </div>
  );
}
