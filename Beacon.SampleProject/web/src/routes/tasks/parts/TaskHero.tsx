import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';

interface TaskHeroProps {
  taskId: number;
  resolved: boolean;
  subscriptionName: string;
  ageLabel: string;
  slaRemainingLabel: string | null;
  onAssign?: () => void;
  onSnooze?: () => void;
  onResolve?: () => void;
  resolveBusy?: boolean;
}

/**
 * Signal hero variant — eyebrow breadcrumb + verb-emphasis title +
 * status pills + actions row. Mirrors `task-detail.jsx`.
 */
export function TaskHero({
  taskId,
  resolved,
  subscriptionName,
  ageLabel,
  slaRemainingLabel,
  onAssign,
  onSnooze,
  onResolve,
  resolveBusy,
}: TaskHeroProps) {
  return (
    <div className="page-hero">
      <div className="page-hero__inner">
        <div className="page-hero__main">
          <div className="page-hero__eyebrow">
            <Link to="/tasks" style={{ color: 'inherit', textDecoration: 'none' }}>Tasks</Link>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#{taskId}</span>
            <span className="beacon-hero__sep">·</span>
            {resolved
              ? <span className="pill pill--ok"><span className="pill__dot" />RESOLVED</span>
              : <span className="pill pill--warn"><span className="pill__dot" />OPEN</span>}
          </div>
          <h1 className="page-hero__title">
            {resolved ? 'Resolved' : 'Investigating'} <span className="page-hero__word">{subscriptionName}</span>
          </h1>
          <p className="page-hero__sub">
            From subscription <span className="mono">{subscriptionName}</span>
            {' · '}open for <span className="mono">{ageLabel}</span>
            {slaRemainingLabel ? <> {' · '}SLA breach in <span className="mono">{slaRemainingLabel}</span></> : null}
          </p>
        </div>
        <div className="page-hero__actions">
          {!resolved && onAssign && (
            <button type="button" className="btn" onClick={onAssign}>
              <Icon.Users size={14} className="btn__icon" /> Assign to me
            </button>
          )}
          {!resolved && onSnooze && (
            <button type="button" className="btn" onClick={onSnooze}>
              <Icon.Bell size={14} className="btn__icon" /> Snooze
            </button>
          )}
          {!resolved && onResolve && (
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

export type { TaskHeroProps };
export type _TaskHeroChildren = ReactNode;
