import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
import type { TaskDetail } from '../queries';

interface RightRailProps {
  task: TaskDetail;
  relatedResolvedCount: number;
}

export function RightRail({ task, relatedResolvedCount }: RightRailProps) {
  const noRecipients = task.notificationCount === 0;

  return (
    <aside className="q-aside">
      <div className="card">
        <div className="card__head">
          <Icon.Lightbulb size={15} className="muted" />
          <h3 className="card__title">Suggested next steps</h3>
          <span className="card__sub">heuristic</span>
        </div>
        <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {!task.resolved && noRecipients && (
            <NextStep
              tone="warn"
              icon={<Icon.Bell size={13} />}
              title="Wire a recipient"
              sub={`Notifications have not been delivered — no recipients on subscription ${task.subscriptionName}.`}
            />
          )}
          {!task.resolved && (
            <NextStep
              tone="info"
              icon={<Icon.Users size={13} />}
              title="Claim ownership"
              sub="Task is unassigned. Assigning yourself stops further auto-pings."
            />
          )}
          {!task.resolved && relatedResolvedCount >= 2 && (
            <NextStep
              tone="ok"
              icon={<Icon.Check size={13} />}
              title="Mark as expected"
              sub={`${relatedResolvedCount} prior tasks from this subscription auto-closed.`}
            />
          )}
          {task.resolved && (
            <NextStep
              tone="ok"
              icon={<Icon.Check size={13} />}
              title="Subscription healthy"
              sub="No further action required."
            />
          )}
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Users size={15} className="muted" />
          <h3 className="card__title">People</h3>
        </div>
        <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <PersonRow
            role="Resolved by"
            name={task.resolvedByUserName ?? 'Not resolved'}
            sub={task.resolvedByUserName ? 'closed this task' : 'still open'}
            muted={!task.resolvedByUserName}
          />
          <PersonRow
            role="Source"
            name={task.aiActorName ?? 'User-defined'}
            sub={task.aiActorName ? 'managed by AI actor' : 'created by a user'}
            muted={!task.aiActorName}
          />
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Branch size={15} className="muted" />
          <h3 className="card__title">Source context</h3>
        </div>
        <div className="checks">
          <Check
            tone={task.cronExpression ? 'ok' : 'pending'}
            title={task.cronExpression ? 'Subscription healthy' : 'Subscription cron unknown'}
            detail={task.cronExpression ? `${task.subscriptionName} · cron ${task.cronExpression}` : task.subscriptionName}
          />
          <Check
            tone={task.lastExecutionAt ? 'ok' : 'pending'}
            title={task.lastExecutionAt ? 'Last execution recorded' : 'No executions recorded'}
            detail={task.lastExecutionAt ?? '—'}
          />
          <Check
            tone={noRecipients ? 'warn' : 'ok'}
            title={noRecipients ? 'No notifications sent' : `${task.notificationCount} notification(s) sent`}
            detail={noRecipients ? 'check recipients on subscription' : 'recipients are wired'}
          />
        </div>
      </div>

      <div className="callout">
        <Icon.Lightbulb size={16} className="callout__icon" />
        <div>
          <div className="callout__title">Tip · keyboard shortcuts</div>
          <div className="callout__sub">
            <span className="kbd">R</span> resolve · <span className="kbd">A</span> assign ·{' '}
            <span className="kbd">C</span> comment
          </div>
        </div>
      </div>
    </aside>
  );
}

function NextStep({
  tone, icon, title, sub,
}: { tone: 'warn' | 'ok' | 'info'; icon: ReactNode; title: string; sub: string }) {
  return (
    <div className={`next-step next-step--${tone}`}>
      <span className="next-step__icon">{icon}</span>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="next-step__title">{title}</div>
        <div className="next-step__sub">{sub}</div>
      </div>
    </div>
  );
}

function PersonRow({
  role, name, sub, muted,
}: { role: string; name: string; sub: string; muted?: boolean }) {
  return (
    <div className="person-row">
      <div className={`avatar avatar--sm${muted ? ' avatar--muted' : ''}`}>
        {muted ? <Icon.Users size={12} /> : initials(name)}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="person-row__role">{role}</div>
        <div className="person-row__name">{name}</div>
        <div className="person-row__sub">{sub}</div>
      </div>
    </div>
  );
}

function Check({
  tone, title, detail,
}: { tone: 'ok' | 'warn' | 'pending'; title: string; detail: string }) {
  return (
    <div className="check">
      <span className={`check__icon check__icon--${tone}`}>
        {tone === 'ok' ? <Icon.Check size={11} /> : tone === 'warn' ? <Icon.Alert size={11} /> : <Icon.Clock size={11} />}
      </span>
      <div className="check__main">
        <div>{title}</div>
        <div className="check__detail">{detail}</div>
      </div>
    </div>
  );
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  const first = parts[0]?.[0] ?? '';
  const second = parts[1]?.[0] ?? '';
  return (first + second).toUpperCase() || '·';
}
