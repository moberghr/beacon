import { Icon } from '@/components/Icon';
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
    <div className="save-bar">
      <span className="save-bar__hint">
        {task.resolved
          ? <span className="pill pill--ok"><span className="pill__dot" />RESOLVED</span>
          : <span className="pill pill--warn"><span className="pill__dot" />OPEN</span>}
        <span>
          Created <span className="mono">{ageLabel}</span> ago
          {slaRemainingLabel ? <> · SLA in <span className="mono">{slaRemainingLabel}</span>.</> : '.'}
        </span>
      </span>
      <div className="spacer" />
      {!task.resolved && (
        <span className="save-bar__hint">
          <span className="kbd">R</span><span>resolve</span>
          <span style={{ marginLeft: 8 }}><span className="kbd">A</span> assign</span>
          <span style={{ marginLeft: 8 }}><span className="kbd">S</span> snooze</span>
          <span style={{ marginLeft: 8 }}><span className="kbd">C</span> comment</span>
        </span>
      )}
      {!task.resolved && (
        <button type="button" className="btn" onClick={onSnooze1h} disabled={snooze.isPending}>
          <Icon.Bell size={14} className="btn__icon" /> Snooze 1h
        </button>
      )}
      {!task.resolved && currentUser?.userId && (
        <button type="button" className="btn" onClick={onAssignClick} disabled={assign.isPending}>
          <Icon.Users size={14} className="btn__icon" /> {assignedToMe ? 'Unassign me' : 'Assign to me'}
        </button>
      )}
      {!task.resolved && onResolve && (
        <button type="button" className="btn btn--primary" onClick={onResolve} disabled={resolveBusy}>
          <Icon.Check size={14} className="btn__icon" /> Resolve task
        </button>
      )}
    </div>
  );
}
