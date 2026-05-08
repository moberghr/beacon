import { Icon } from '@/components/Icon';

interface TaskSaveBarProps {
  resolved: boolean;
  ageLabel: string;
  slaRemainingLabel: string | null;
  onAssign?: () => void;
  onSnooze?: () => void;
  onResolve?: () => void;
  resolveBusy?: boolean;
}

export function TaskSaveBar({
  resolved,
  ageLabel,
  slaRemainingLabel,
  onAssign,
  onSnooze,
  onResolve,
  resolveBusy,
}: TaskSaveBarProps) {
  return (
    <div className="save-bar">
      <span className="save-bar__hint">
        {resolved
          ? <span className="pill pill--ok"><span className="pill__dot" />RESOLVED</span>
          : <span className="pill pill--warn"><span className="pill__dot" />OPEN</span>}
        <span>
          Created <span className="mono">{ageLabel}</span> ago
          {slaRemainingLabel ? <> · SLA in <span className="mono">{slaRemainingLabel}</span>.</> : '.'}
        </span>
      </span>
      <div className="spacer" />
      {!resolved && (
        <span className="save-bar__hint">
          <span className="kbd">R</span><span>resolve</span>
        </span>
      )}
      {!resolved && onSnooze && (
        <button type="button" className="btn" onClick={onSnooze}>
          <Icon.Bell size={14} className="btn__icon" /> Snooze 1h
        </button>
      )}
      {!resolved && onAssign && (
        <button type="button" className="btn" onClick={onAssign}>
          <Icon.Users size={14} className="btn__icon" /> Assign to me
        </button>
      )}
      {!resolved && onResolve && (
        <button type="button" className="btn btn--primary" onClick={onResolve} disabled={resolveBusy}>
          <Icon.Check size={14} className="btn__icon" /> Resolve task
        </button>
      )}
    </div>
  );
}
