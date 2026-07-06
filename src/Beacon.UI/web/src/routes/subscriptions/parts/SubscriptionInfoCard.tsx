import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Info, Settings, SlidersHorizontal } from 'lucide-react';
import {
  Card,
  CardBody,
  CardHead,
  CardTitle,
  Pill,
} from '@/components/beacon';
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
      <Card>
        <CardHead>
          <Info className="size-3.5 text-text-muted" />
          <CardTitle>Query configuration</CardTitle>
        </CardHead>
        <CardBody>
          <KvList>
            <KV
              label="Query"
              value={
                <Link
                  to={`/queries/${subscription.queryId}`}
                  className="mono text-brand-600"
                >
                  {subscription.queryName}
                </Link>
              }
            />
            <KV
              label="Schedule"
              value={
                <span className="inline-flex items-center gap-1.5">
                  <span>{subscription.cronDescription || '—'}</span>
                  <Pill className="mono normal-case tracking-normal">
                    {subscription.cronExpression || '—'}
                  </Pill>
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
                  <span className="inline-flex items-center gap-1.5">
                    <span className="mono">≥ {subscription.minimumRowCount} rows</span>
                    <Pill tone="warn">active</Pill>
                  </span>
                ) : (
                  <span className="text-text-subtle">none</span>
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
          </KvList>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Settings className="size-3.5 text-text-muted" />
          <CardTitle>Notification settings</CardTitle>
        </CardHead>
        <CardBody>
          <KvList>
            <KV
              label="Send notification"
              value={
                trigger ? (
                  <span className="inline-flex items-center gap-2">
                    <Pill tone="info">{trigger.label}</Pill>
                    <span className="text-text-muted text-xs">{trigger.description}</span>
                  </span>
                ) : (
                  <span className="text-text-subtle">unknown</span>
                )
              }
            />
            <KV
              label="Include attachment"
              value={
                subscription.includeAttachment ? (
                  <span className="inline-flex items-center gap-1.5">
                    <Pill tone="ok">enabled</Pill>
                    {fileTypeLabel && (
                      <span className="mono text-text-muted text-xs">{fileTypeLabel}</span>
                    )}
                  </span>
                ) : (
                  <Pill>disabled</Pill>
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
          </KvList>
        </CardBody>
      </Card>

      {visibleParams.length > 0 && (
        <Card>
          <CardHead>
            <SlidersHorizontal className="size-3.5 text-text-muted" />
            <CardTitle>Query parameters</CardTitle>
          </CardHead>
          <CardBody>
            <KvList>
              {visibleParams.map(p => (
                <KV
                  key={p.queryPlaceholder ?? ''}
                  label={p.queryPlaceholder ?? ''}
                  value={
                    p.value
                      ? <span className="mono">{p.value}</span>
                      : <span className="text-text-subtle">—</span>
                  }
                />
              ))}
            </KvList>
          </CardBody>
        </Card>
      )}
    </>
  );
}

function KvList({ children }: { children: ReactNode }) {
  return (
    <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
      {children}
    </dl>
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

function TogglePill({ on, caption }: { on: boolean; caption: string }) {
  return (
    <span className="inline-flex items-center gap-2">
      <Pill tone={on ? 'ok' : 'neutral'}>{on ? 'enabled' : 'disabled'}</Pill>
      <span className="text-text-muted text-xs">{caption}</span>
    </span>
  );
}
