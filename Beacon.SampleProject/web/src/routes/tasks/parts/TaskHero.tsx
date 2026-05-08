import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { useAuth } from '@/auth/useAuth';
import {
  useAssignTask,
  useSnoozeTask,
  type TaskDetail,
  type TaskPriority,
} from '../queries';

interface TaskHeroProps {
  task: TaskDetail;
  ageLabel: string;
  slaRemainingLabel: string | null;
  onResolve?: () => void;
  resolveBusy?: boolean;
}

const PRIORITY_LABEL: Record<TaskPriority, string> = {
  1: 'P1 · CRITICAL',
  2: 'P2 · HIGH',
  3: 'P3 · NORMAL',
  4: 'P4 · LOW',
};

const PRIORITY_PILL_CLASS: Record<TaskPriority, string> = {
  1: 'pill--crit',
  2: 'pill--warn',
  3: 'pill--neutral',
  4: 'pill--ok',
};

/**
 * Signal hero variant — eyebrow breadcrumb + verb-emphasis title +
 * status pills + actions row. All mocks removed; assign/snooze/priority
 * pills are wired to real backend state.
 */
export function TaskHero({
  task,
  ageLabel,
  slaRemainingLabel,
  onResolve,
  resolveBusy,
}: TaskHeroProps) {
  const { data: currentUser } = useAuth();
  const assign = useAssignTask(task.id);
  const snooze = useSnoozeTask(task.id);

  const [assignOpen, setAssignOpen] = useState(false);
  const [snoozeOpen, setSnoozeOpen] = useState(false);

  const assignedToMe =
    currentUser?.userId != null &&
    task.assigneeUserId != null &&
    task.assigneeUserId === currentUser.userId;

  const snoozedNow =
    task.snoozedUntil != null && new Date(task.snoozedUntil).getTime() > Date.now();

  const statusPill = task.resolved ? (
    <span className="pill pill--ok"><span className="pill__dot" />RESOLVED</span>
  ) : snoozedNow ? (
    <span className="pill pill--neutral"><span className="pill__dot" />SNOOZED</span>
  ) : (
    <span className="pill pill--warn"><span className="pill__dot" />OPEN</span>
  );

  const sourceLabel = task.aiActorName ?? 'USER-DEFINED';

  return (
    <div className="page-hero">
      <div className="page-hero__inner">
        <div className="page-hero__main">
          <div className="page-hero__eyebrow">
            <Link to="/tasks" style={{ color: 'inherit', textDecoration: 'none' }}>Tasks</Link>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#{task.id}</span>
            <span className="beacon-hero__sep">·</span>
            {statusPill}
            <span className="beacon-hero__sep">·</span>
            <span className={`pill ${PRIORITY_PILL_CLASS[task.priority]}`}>
              <span className="pill__dot" />{PRIORITY_LABEL[task.priority]}
            </span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--neutral"><span className="pill__dot" />{sourceLabel.toUpperCase()}</span>
          </div>
          <h1 className="page-hero__title">
            {task.resolved ? 'Resolved' : 'Investigating'}{' '}
            <span className="page-hero__word">{task.subscriptionName}</span>
          </h1>
          <p className="page-hero__sub">
            From subscription <span className="mono">{task.subscriptionName}</span>
            {' · '}open for <span className="mono">{ageLabel}</span>
            {slaRemainingLabel ? <> {' · '}SLA breach in <span className="mono">{slaRemainingLabel}</span></> : null}
            {snoozedNow && task.snoozedUntil ? (
              <> {' · '}snoozed until <span className="mono">{new Date(task.snoozedUntil).toLocaleString()}</span></>
            ) : null}
          </p>
        </div>
        <div className="page-hero__actions" style={{ position: 'relative' }}>
          {!task.resolved && (
            <Popover
              open={assignOpen}
              onOpenChange={setAssignOpen}
              trigger={
                <button type="button" className="btn" onClick={() => setAssignOpen(o => !o)}>
                  <Icon.Users size={14} className="btn__icon" />{' '}
                  {assignedToMe ? 'Unassign me' : task.assigneeUserName ? 'Reassign' : 'Assign'}
                </button>
              }
            >
              {currentUser?.userId && !assignedToMe && (
                <PopItem
                  onClick={() => {
                    setAssignOpen(false);
                    assign.mutate({ assigneeUserId: currentUser.userId });
                  }}
                  disabled={assign.isPending}
                  icon={<Icon.Users size={13} />}
                  label="Assign to me"
                />
              )}
              {task.assigneeUserId && (
                <PopItem
                  onClick={() => {
                    setAssignOpen(false);
                    assign.mutate({ assigneeUserId: null });
                  }}
                  disabled={assign.isPending}
                  icon={<Icon.Alert size={13} />}
                  label={assignedToMe ? 'Unassign me' : `Unassign ${task.assigneeUserName ?? task.assigneeUserId}`}
                />
              )}
            </Popover>
          )}

          {!task.resolved && (
            <Popover
              open={snoozeOpen}
              onOpenChange={setSnoozeOpen}
              trigger={
                <button type="button" className="btn" onClick={() => setSnoozeOpen(o => !o)}>
                  <Icon.Bell size={14} className="btn__icon" />{' '}
                  {snoozedNow ? 'Snoozed' : 'Snooze'}
                </button>
              }
            >
              {snoozedNow && (
                <PopItem
                  onClick={() => {
                    setSnoozeOpen(false);
                    snooze.mutate({ snoozeUntil: null });
                  }}
                  disabled={snooze.isPending}
                  icon={<Icon.Refresh size={13} />}
                  label="Wake now"
                />
              )}
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(1) });
                }}
                disabled={snooze.isPending}
                icon={<Icon.Clock size={13} />}
                label="1 hour"
              />
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(4) });
                }}
                disabled={snooze.isPending}
                icon={<Icon.Clock size={13} />}
                label="4 hours"
              />
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(24) });
                }}
                disabled={snooze.isPending}
                icon={<Icon.Clock size={13} />}
                label="24 hours"
              />
              <SnoozeCustomRow
                onPick={iso => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: iso });
                }}
                disabled={snooze.isPending}
              />
            </Popover>
          )}

          {!task.resolved && onResolve && (
            <button
              type="button"
              className="btn btn--primary"
              onClick={onResolve}
              disabled={resolveBusy}
            >
              <Icon.Check size={14} className="btn__icon" /> Resolve task
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function addHoursISO(hours: number): string {
  return new Date(Date.now() + hours * 3_600_000).toISOString();
}

function Popover({
  open,
  onOpenChange,
  trigger,
  children,
}: {
  open: boolean;
  onOpenChange: (b: boolean) => void;
  trigger: ReactNode;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onOpenChange(false);
      }
    };
    window.addEventListener('mousedown', handler);
    return () => window.removeEventListener('mousedown', handler);
  }, [open, onOpenChange]);

  return (
    <div ref={ref} style={{ position: 'relative', display: 'inline-block' }}>
      {trigger}
      {open && (
        <div
          role="menu"
          style={{
            position: 'absolute',
            top: 'calc(100% + 4px)',
            right: 0,
            background: 'var(--surface)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
            minWidth: 220,
            padding: 4,
            zIndex: 30,
          }}
        >
          {children}
        </div>
      )}
    </div>
  );
}

function PopItem({
  onClick,
  disabled,
  icon,
  label,
}: {
  onClick: () => void;
  disabled?: boolean;
  icon?: ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        width: '100%',
        background: 'transparent',
        border: 0,
        padding: '6px 10px',
        textAlign: 'left',
        fontSize: 13,
        cursor: disabled ? 'not-allowed' : 'pointer',
        color: 'var(--text)',
        borderRadius: 4,
      }}
      onMouseEnter={e => (e.currentTarget.style.background = 'var(--surface-2)')}
      onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
    >
      {icon && <span className="muted">{icon}</span>}
      <span>{label}</span>
    </button>
  );
}

function SnoozeCustomRow({
  onPick,
  disabled,
}: {
  onPick: (iso: string) => void;
  disabled?: boolean;
}) {
  const [value, setValue] = useState('');
  return (
    <div style={{ padding: '6px 10px', borderTop: '1px solid var(--border)' }}>
      <div className="muted" style={{ fontSize: 11.5, marginBottom: 4 }}>
        Custom (local time)
      </div>
      <div style={{ display: 'flex', gap: 6 }}>
        <input
          type="datetime-local"
          value={value}
          onChange={e => setValue(e.target.value)}
          className="q-input"
          style={{ flex: 1, fontSize: 12.5 }}
          disabled={disabled}
        />
        <button
          type="button"
          className="btn btn--primary"
          disabled={!value || disabled}
          onClick={() => {
            const d = new Date(value);
            if (Number.isNaN(d.getTime())) return;
            if (d.getTime() <= Date.now()) return;
            onPick(d.toISOString());
          }}
        >
          OK
        </button>
      </div>
    </div>
  );
}

export type { TaskHeroProps };
