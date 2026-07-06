import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import {
  AlertTriangle,
  Bell,
  Check,
  Clock,
  RefreshCw,
  Users,
} from 'lucide-react';
import { Button, Input, PageHeader, Pill, type PillProps } from '@/components/beacon';
import { useAuth } from '@/auth/useAuth';
import { TaskPriority } from '@/lib/enums';
import {
  useAssignTask,
  useSnoozeTask,
  type TaskDetail,
} from '../queries';

interface TaskHeroProps {
  task: TaskDetail;
  ageLabel: string;
  slaRemainingLabel: string | null;
  onResolve?: () => void;
  resolveBusy?: boolean;
}

const PRIORITY_LABEL: Record<TaskPriority, string> = {
  [TaskPriority.Critical]: 'P1 · CRITICAL',
  [TaskPriority.High]: 'P2 · HIGH',
  [TaskPriority.Normal]: 'P3 · NORMAL',
  [TaskPriority.Low]: 'P4 · LOW',
};

const PRIORITY_TONE: Record<TaskPriority, PillProps['tone']> = {
  [TaskPriority.Critical]: 'crit',
  [TaskPriority.High]: 'warn',
  [TaskPriority.Normal]: 'neutral',
  [TaskPriority.Low]: 'ok',
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
    <Pill tone="ok" dot>RESOLVED</Pill>
  ) : snoozedNow ? (
    <Pill dot>SNOOZED</Pill>
  ) : (
    <Pill tone="warn" dot>OPEN</Pill>
  );

  const sourceLabel = task.aiActorName ?? 'USER-DEFINED';

  return (
    <PageHeader
      variant="signal"
      eyebrow={
        <>
          <Link to="/tasks" className="hover:text-text">Tasks</Link>
          <span className="eyebrow-sep">/</span>
          <span className="mono normal-case tracking-normal">#{task.id}</span>
          <span className="eyebrow-sep">·</span>
          {statusPill}
          <span className="eyebrow-sep">·</span>
          <Pill tone={PRIORITY_TONE[task.priority]} dot>
            {PRIORITY_LABEL[task.priority]}
          </Pill>
          <span className="eyebrow-sep">·</span>
          <Pill dot>{sourceLabel.toUpperCase()}</Pill>
        </>
      }
      prefix={task.resolved ? 'Resolved' : 'Investigating'}
      emphasis={task.subscriptionName}
      sub={
        <>
          From subscription <span className="mono">{task.subscriptionName}</span>
          {' · '}open for <span className="mono">{ageLabel}</span>
          {slaRemainingLabel ? <> {' · '}SLA breach in <span className="mono">{slaRemainingLabel}</span></> : null}
          {snoozedNow && task.snoozedUntil ? (
            <> {' · '}snoozed until <span className="mono">{new Date(task.snoozedUntil).toLocaleString()}</span></>
          ) : null}
        </>
      }
      actions={
        <>
          {!task.resolved && (
            <Popover
              open={assignOpen}
              onOpenChange={setAssignOpen}
              trigger={
                <Button
                  icon={<Users />}
                  aria-haspopup="menu"
                  aria-expanded={assignOpen}
                  onClick={() => setAssignOpen(o => !o)}
                >
                  {assignedToMe ? 'Unassign me' : task.assigneeUserName ? 'Reassign' : 'Assign'}
                </Button>
              }
            >
              {currentUser?.userId && !assignedToMe && (
                <PopItem
                  onClick={() => {
                    setAssignOpen(false);
                    assign.mutate({ assigneeUserId: currentUser.userId });
                  }}
                  disabled={assign.isPending}
                  icon={<Users className="size-3" />}
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
                  icon={<AlertTriangle className="size-3" />}
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
                <Button
                  icon={<Bell />}
                  aria-haspopup="menu"
                  aria-expanded={snoozeOpen}
                  onClick={() => setSnoozeOpen(o => !o)}
                >
                  {snoozedNow ? 'Snoozed' : 'Snooze'}
                </Button>
              }
            >
              {snoozedNow && (
                <PopItem
                  onClick={() => {
                    setSnoozeOpen(false);
                    snooze.mutate({ snoozeUntil: null });
                  }}
                  disabled={snooze.isPending}
                  icon={<RefreshCw className="size-3" />}
                  label="Wake now"
                />
              )}
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(1) });
                }}
                disabled={snooze.isPending}
                icon={<Clock className="size-3" />}
                label="1 hour"
              />
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(4) });
                }}
                disabled={snooze.isPending}
                icon={<Clock className="size-3" />}
                label="4 hours"
              />
              <PopItem
                onClick={() => {
                  setSnoozeOpen(false);
                  snooze.mutate({ snoozeUntil: addHoursISO(24) });
                }}
                disabled={snooze.isPending}
                icon={<Clock className="size-3" />}
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
            <Button
              variant="primary"
              icon={<Check />}
              onClick={onResolve}
              disabled={resolveBusy}
            >
              Resolve task
            </Button>
          )}
        </>
      }
    />
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
  const menuRef = useRef<HTMLDivElement>(null);

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

  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onOpenChange(false);
        // Return focus to the trigger button.
        ref.current?.querySelector<HTMLElement>('button')?.focus();
        return;
      }
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        const items = Array.from(
          menuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [],
        ).filter(x => !x.disabled);
        if (items.length === 0) return;
        e.preventDefault();
        const idx = items.indexOf(document.activeElement as HTMLButtonElement);
        const next = e.key === 'ArrowDown'
          ? (idx + 1) % items.length
          : (idx - 1 + items.length) % items.length;
        items[next]?.focus();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, onOpenChange]);

  return (
    <div ref={ref} className="relative inline-block">
      {trigger}
      {open && (
        <div
          ref={menuRef}
          role="menu"
          className="absolute top-[calc(100%+4px)] right-0 z-30 min-w-[220px] p-1 bg-surface border border-border rounded-sm shadow-pop"
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
      role="menuitem"
      onClick={onClick}
      disabled={disabled}
      className="flex items-center gap-2 w-full bg-transparent border-0 px-2.5 py-1.5 text-left text-sm text-text rounded-xs hover:bg-surface-2 disabled:opacity-50 disabled:cursor-not-allowed"
    >
      {icon && <span className="text-text-muted">{icon}</span>}
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
    <div className="px-2.5 py-1.5 border-t border-border">
      <div className="text-text-muted text-xs mb-1">Custom (local time)</div>
      <div className="flex gap-1.5">
        <Input
          type="datetime-local"
          value={value}
          onChange={e => setValue(e.target.value)}
          className="flex-1 text-xs"
          disabled={disabled}
        />
        <Button
          variant="primary"
          size="sm"
          disabled={!value || disabled}
          onClick={() => {
            const d = new Date(value);
            if (Number.isNaN(d.getTime())) return;
            if (d.getTime() <= Date.now()) return;
            onPick(d.toISOString());
          }}
        >
          OK
        </Button>
      </div>
    </div>
  );
}

export type { TaskHeroProps };
