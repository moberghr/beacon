import type { ReactNode } from 'react';
import { Icon } from '@/components/Icon';
import type {
  SubscriptionDetail,
  SubscriptionExecutionEntry,
} from '../queries';
import { NOTIFICATION_STATUS_LABEL } from '../queries';

interface RightRailProps {
  subscription: SubscriptionDetail;
  executions: SubscriptionExecutionEntry[] | undefined;
}

export function RightRail({ subscription, executions }: RightRailProps) {
  const list = executions ?? [];
  const totalExecs = list.length;
  const failed = list.filter(x => x.status === 5 || x.status === 7).length;
  const noRecipients = subscription.recipients.length === 0;
  const neverRun = totalExecs === 0;
  const anomalyOff = subscription.anomalyConfig?.enabled !== true;
  const isAi = subscription.aiActorId != null;

  return (
    <aside className="q-aside">
      <div className="card">
        <div className="card__head">
          <Icon.Lightbulb size={15} className="muted" />
          <h3 className="card__title">Suggested next steps</h3>
          <span className="card__sub">heuristic</span>
        </div>
        <div
          className="card__body"
          style={{ display: 'flex', flexDirection: 'column', gap: 8 }}
        >
          {neverRun && (
            <NextStep
              tone="info"
              icon={<Icon.Bolt size={13} />}
              title="Run once to validate"
              sub="No executions on record. Use Test now to verify the schedule and recipients."
            />
          )}
          {noRecipients && (
            <NextStep
              tone="warn"
              icon={<Icon.Users size={13} />}
              title="Add a recipient"
              sub={
                subscription.createTasks
                  ? 'Tasks will still be created, but no one will be notified directly.'
                  : 'Without recipients, this subscription will not deliver anywhere.'
              }
            />
          )}
          {failed > 0 && (
            <NextStep
              tone="warn"
              icon={<Icon.Alert size={13} />}
              title="Review recent failures"
              sub={`${failed} failed/timed-out execution${failed === 1 ? '' : 's'} in the recent window.`}
            />
          )}
          {anomalyOff && totalExecs >= 5 && (
            <NextStep
              tone="info"
              icon={<Icon.Activity size={13} />}
              title="Enable anomaly detection"
              sub="Enough history exists to learn a baseline. Configure thresholds in the settings tab."
            />
          )}
          {!neverRun && !noRecipients && failed === 0 && !anomalyOff && (
            <NextStep
              tone="ok"
              icon={<Icon.Check size={13} />}
              title="Subscription is healthy"
              sub="Recent executions succeeded and detection is wired."
            />
          )}
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Users size={15} className="muted" />
          <h3 className="card__title">Owner &amp; query</h3>
        </div>
        <div
          className="card__body"
          style={{ display: 'flex', flexDirection: 'column', gap: 10 }}
        >
          <PersonRow
            role="Owner"
            name={subscription.aiActorName ?? 'User-defined'}
            sub={isAi ? 'AI actor — managed' : 'manual'}
            muted={!isAi}
          />
          <PersonRow
            role="Query"
            name={subscription.queryName}
            sub={`#${subscription.queryId}`}
          />
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Clock size={15} className="muted" />
          <h3 className="card__title">Latest run</h3>
        </div>
        <div
          className="card__body"
          style={{ display: 'flex', flexDirection: 'column', gap: 8 }}
        >
          {list.length === 0 ? (
            <span className="muted">No runs recorded yet.</span>
          ) : (
            (() => {
              const latest = list
                .slice()
                .sort((a, b) => +new Date(b.createdTime) - +new Date(a.createdTime))[0];
              return (
                <>
                  <Check
                    tone={latest.status === 2 ? 'ok' : latest.status === 5 || latest.status === 7 ? 'warn' : 'pending'}
                    title={NOTIFICATION_STATUS_LABEL[latest.status] ?? 'unknown'}
                    detail={`${latest.resultCount.toLocaleString()} result${latest.resultCount === 1 ? '' : 's'} · ${Math.round(latest.executionTimeMs)} ms`}
                  />
                  <Check
                    tone={latest.recipientNames.length > 0 ? 'ok' : 'pending'}
                    title={`${latest.recipientNames.length} recipient${latest.recipientNames.length === 1 ? '' : 's'} notified`}
                    detail={latest.recipientNames.slice(0, 2).join(', ') || '—'}
                  />
                </>
              );
            })()
          )}
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
