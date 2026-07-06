import { Bell, Check, Users } from 'lucide-react';
import { Button, Kbd, Pill } from '@/components/beacon';
import { useAuth } from '@/auth/useAuth';
import { useAssignTask, useSnoozeTask, type TaskDetail } from '../queries';

interface TaskSaveBarProps {
  task: TaskDetail;
  ageLabel: string;
  slaRemainingLabel: string | null;
  onResolve?: () => void;
  resolveBusy?: boolean;
}

export function TaskSaveBar({
  task,
  ageLabel,
  slaRemainingLabel,
  onResolve,
  resolveBusy,
}: TaskSaveBarProps) {
  const { data: currentUser } = useAuth();
  const assign = useAssignTask(task.id);
  const snooze = useSnoozeTask(task.id);

  const assignedToMe =
    currentUser?.userId != null && task.assigneeUserId === currentUser.userId;

  const onAssignClick = () => {
    if (!currentUser?.userId) return;
    assign.mutate({ assigneeUserId: assignedToMe ? null : currentUser.userId });
  };

  const onSnooze1h = () => {
    snooze.mutate({ snoozeUntil: new Date(Date.now() + 3_600_000).toISOString() });
  };

  return (
    <div className="flex items-center gap-2 px-5 py-3 border-t border-border bg-surface-2 rounded-md">
      <span className="flex items-center gap-2 text-xs text-text-muted">
        {task.resolved
          ? <Pill tone="ok" dot>RESOLVED</Pill>
          : <Pill tone="warn" dot>OPEN</Pill>}
        <span>
          Created <span className="mono">{ageLabel}</span> ago
          {slaRemainingLabel ? <> · SLA in <span className="mono">{slaRemainingLabel}</span>.</> : '.'}
        </span>
      </span>
      <div className="ml-auto flex items-center gap-1.5">
        {!task.resolved && (
          <span className="text-2xs text-text-muted flex items-center gap-1.5">
            <Kbd>R</Kbd><span>resolve</span>
            <Kbd>A</Kbd><span>assign</span>
            <Kbd>S</Kbd><span>snooze</span>
            <Kbd>C</Kbd><span>comment</span>
          </span>
        )}
        {!task.resolved && (
          <Button icon={<Bell />} onClick={onSnooze1h} disabled={snooze.isPending}>
            Snooze 1h
          </Button>
        )}
        {!task.resolved && currentUser?.userId && (
          <Button icon={<Users />} onClick={onAssignClick} disabled={assign.isPending}>
            {assignedToMe ? 'Unassign me' : 'Assign to me'}
          </Button>
        )}
        {!task.resolved && onResolve && (
          <Button variant="primary" icon={<Check />} onClick={onResolve} disabled={resolveBusy}>
            Resolve task
          </Button>
        )}
      </div>
    </div>
  );
}
