import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import {
  FILE_TYPE_LABEL,
  NOTIFICATION_TRIGGER_LABEL,
  type SubscriptionDetail,
} from '../queries';

interface SubscriptionInfoCardProps {
  subscription: SubscriptionDetail;
}

/**
 * Two-column "Overview" replacing the Blazor MudTabPanel `Overview` tab.
 * Left = query configuration. Right = notification settings + parameters.
 */
export function SubscriptionInfoCard({ subscription }: SubscriptionInfoCardProps) {
  const trigger = NOTIFICATION_TRIGGER_LABEL[subscription.notificationTrigger];
  const fileTypeLabel = subscription.resultAttachmentType != null
    ? FILE_TYPE_LABEL[subscription.resultAttachmentType] ?? '—'
    : null;

  const visibleParams = subscription.parameters.filter(
    p => (p.queryPlaceholder ?? '').trim() !== '',
  );

  return (
    <>
      <div className="card">
        <div className="card__head">
          <Icon.Info size={15} className="muted" />
          <h3 className="card__title">Query configuration</h3>
        </div>
        <div className="card__body">
          <div className="kv">
            <KV
              label="Query"
              value={
                <Link
                  to={`/queries/${subscription.queryId}`}
                  className="mono"
                  style={{ color: 'var(--brand-600)' }}
                >
                  {subscription.queryName}
                </Link>
              }
            />
            <KV
              label="Schedule"
              value={
                <span>
                  <span>{subscription.cronDescription || '—'}</span>
                  <span
                    className="pill pill--neutral mono"
                    style={{ fontSize: 10, marginLeft: 6 }}
                  >
                    {subscription.cronExpression || '—'}
                  </span>
                </span>
              }
            />
            <KV
              label="Result limit"
              value={
                <span className="mono">
                  {subscription.maxRows != null ? `${subscription.maxRows} rows` : 'no limit'}
                </span>
              }
            />
            <KV
              label="Min row threshold"
              value={
                subscription.minimumRowCount != null ? (
                  <>
                    <span className="mono">≥ {subscription.minimumRowCount} rows</span>
                    <span
                      className="pill pill--warn mono"
                      style={{ fontSize: 10, marginLeft: 6 }}
                    >
                      active
                    </span>
                  </>
                ) : (
                  <span className="subtle">none</span>
                )
              }
            />
            <KV
              label="Query timeout"
              value={
                <span className="mono">
                  {subscription.timeoutSeconds != null
                    ? `${subscription.timeoutSeconds} seconds`
                    : 'no timeout'}
                </span>
              }
            />
            <KV
              label="Owner"
              value={
                subscription.aiActorName ? (
                  <span className="mono">{subscription.aiActorName} · AI</span>
                ) : (
                  <span className="mono">user-defined</span>
                )
              }
            />
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card__head">
          <Icon.Cog size={15} className="muted" />
          <h3 className="card__title">Notification settings</h3>
        </div>
        <div className="card__body">
          <div className="kv">
            <KV
              label="Send notification"
              value={
                trigger ? (
                  <>
                    <span className="pill pill--info mono" style={{ fontSize: 10 }}>
                      {trigger.label}
                    </span>
                    <span className="muted" style={{ marginLeft: 8 }}>
                      {trigger.description}
                    </span>
                  </>
                ) : (
                  <span className="subtle">unknown</span>
                )
              }
            />
            <KV
              label="Include attachment"
              value={
                subscription.includeAttachment ? (
                  <>
                    <span className="pill pill--ok mono" style={{ fontSize: 10 }}>enabled</span>
                    {fileTypeLabel && (
                      <span className="mono muted" style={{ marginLeft: 6 }}>
                        {fileTypeLabel}
                      </span>
                    )}
                  </>
                ) : (
                  <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>disabled</span>
                )
              }
            />
            <KV
              label="Show query"
              value={<TogglePill on={subscription.showQuery} caption="SQL in notifications" />}
            />
            <KV
              label="Store results"
              value={<TogglePill on={subscription.storeResults} caption="persist query results" />}
            />
            <KV
              label="Create tasks"
              value={
                <TogglePill
                  on={subscription.createTasks}
                  caption="auto-create tracking tasks"
                />
              }
            />
          </div>
        </div>
      </div>

      {visibleParams.length > 0 && (
        <div className="card">
          <div className="card__head">
            <Icon.Sliders size={15} className="muted" />
            <h3 className="card__title">Query parameters</h3>
          </div>
          <div className="card__body">
            <div className="kv">
              {visibleParams.map(p => (
                <KV
                  key={p.queryPlaceholder ?? ''}
                  label={p.queryPlaceholder ?? ''}
                  value={
                    p.value
                      ? <span className="mono">{p.value}</span>
                      : <span className="subtle">—</span>
                  }
                />
              ))}
            </div>
          </div>
        </div>
      )}
    </>
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

function TogglePill({ on, caption }: { on: boolean; caption: string }) {
  return (
    <>
      <span
        className={`pill ${on ? 'pill--ok' : 'pill--neutral'} mono`}
        style={{ fontSize: 10 }}
      >
        {on ? 'enabled' : 'disabled'}
      </span>
      <span className="muted" style={{ marginLeft: 8 }}>{caption}</span>
    </>
  );
}
