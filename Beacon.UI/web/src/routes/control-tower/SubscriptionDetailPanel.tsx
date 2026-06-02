import { Link } from 'react-router-dom';
import { AlertTriangle, ArrowRight, Loader2, X } from 'lucide-react';
import { Pill, Card } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { useControlTowerSubscriptionDetail } from './queries';
import {
  NotificationStatus,
  type ControlTowerExecutionItem,
  type ControlTowerSubscriptionHealthData,
} from './api';

const STATUS_PILL: Record<NotificationStatus, { label: string; tone: 'ok' | 'warn' | 'crit' | 'info' | 'neutral' }> = {
  [NotificationStatus.Created]: { label: 'Created', tone: 'info' },
  [NotificationStatus.NotificationSent]: { label: 'Sent', tone: 'ok' },
  [NotificationStatus.NotificationSilenced]: { label: 'Silenced', tone: 'neutral' },
  [NotificationStatus.NoResults]: { label: 'No results', tone: 'ok' },
  [NotificationStatus.Timeout]: { label: 'Timeout', tone: 'crit' },
  [NotificationStatus.BelowThreshold]: { label: 'Below threshold', tone: 'neutral' },
  [NotificationStatus.Failed]: { label: 'Failed', tone: 'crit' },
};

const SEVERITY_TONE: Record<string, 'ok' | 'warn' | 'crit' | 'info' | 'neutral'> = {
  Low: 'info',
  Medium: 'warn',
  High: 'crit',
  Critical: 'crit',
};

function StatusPill({ status }: { status: NotificationStatus }) {
  const map = STATUS_PILL[status] ?? { label: '?', tone: 'neutral' as const };
  return <Pill tone={map.tone}>{map.label}</Pill>;
}

function ExecutionRow({ exec }: { exec: ControlTowerExecutionItem }) {
  const failed = exec.errorMessage != null && exec.errorMessage !== '';
  return (
    <div className="flex flex-col gap-1 py-2 px-3 border-b border-border last:border-b-0">
      <div className="flex items-center gap-2 text-xs">
        <StatusPill status={exec.notificationStatus} />
        <span className="text-text" title={formatDateTime(exec.createdTime)}>
          {formatRelativeTime(exec.createdTime)}
        </span>
        <span className="text-text-muted">·</span>
        <span className="text-text-muted">{formatNumber(exec.resultCount)} rows</span>
        <span className="text-text-muted">·</span>
        <span className="text-text-muted">{Math.round(exec.executionTimeMs)}ms</span>
      </div>
      {failed && (
        <div className="text-2xs text-crit font-mono break-all bg-crit-bg/40 rounded-xs px-2 py-1">
          {exec.errorMessage}
        </div>
      )}
    </div>
  );
}

export function SubscriptionDetailPanel({
  row,
  timeRangeDays,
  onClose,
}: {
  row: ControlTowerSubscriptionHealthData;
  timeRangeDays: number;
  onClose: () => void;
}) {
  const { data: detail, isLoading, isError, error } = useControlTowerSubscriptionDetail(
    row.subscriptionId,
    timeRangeDays,
  );

  return (
    <div
      className="fixed inset-0 z-40 bg-black/35 backdrop-blur-sm flex justify-end"
      onClick={onClose}
    >
      <aside
        className="bg-surface border-l border-border w-full max-w-[640px] h-full overflow-y-auto shadow-pop"
        onClick={e => e.stopPropagation()}
      >
        <header className="sticky top-0 bg-surface border-b border-border px-5 py-4 flex items-start gap-3 z-10">
          <div className="flex-1 min-w-0">
            <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">
              Subscription · #{row.subscriptionId}
            </div>
            <h2 className="text-lg font-semibold leading-tight tracking-tight truncate">
              {row.queryName}
            </h2>
            {row.folderPath && (
              <div className="text-xs text-text-muted mt-1 truncate">{row.folderPath}</div>
            )}
            <div className="flex flex-wrap items-center gap-1.5 mt-2">
              {row.hasAnomalyDetection && <Pill tone="info">Anomaly detection</Pill>}
              {row.aiActorName && <Pill tone="info">AI · {row.aiActorName}</Pill>}
              {row.createTasks && <Pill tone="neutral">Creates tasks</Pill>}
              {row.storeResults && <Pill tone="neutral">Stores results</Pill>}
            </div>
          </div>
          <button
            onClick={onClose}
            aria-label="Close"
            className="shrink-0 size-8 grid place-items-center rounded-sm text-text-muted hover:bg-surface-2 hover:text-text"
          >
            <X size={16} />
          </button>
        </header>

        <div className="p-5 flex flex-col gap-4">
          <div className="flex flex-wrap gap-2">
            <Link
              to={`/subscriptions/${row.subscriptionId}`}
              className="inline-flex items-center gap-1.5 bg-surface text-text border border-border-strong rounded-sm text-sm px-2.5 py-1.5 hover:bg-surface-2"
            >
              Open subscription <ArrowRight size={14} />
            </Link>
            {row.unresolvedTaskCount > 0 && (
              <Link
                to={`/tasks?subscriptionId=${row.subscriptionId}`}
                className="inline-flex items-center gap-1.5 bg-transparent text-text-muted border border-transparent rounded-sm text-sm px-2.5 py-1.5 hover:bg-surface-2 hover:text-text"
              >
                {row.unresolvedTaskCount} open task{row.unresolvedTaskCount === 1 ? '' : 's'}
              </Link>
            )}
          </div>

          {isLoading && (
            <div className="flex items-center gap-2 text-text-muted text-sm py-6 justify-center">
              <Loader2 className="animate-spin" size={16} /> Loading detail…
            </div>
          )}

          {isError && (
            <EmptyState
              icon={<AlertTriangle />}
              title="Failed to load detail"
              description={error instanceof Error ? error.message : 'Unknown error'}
            />
          )}

          {detail && (
            <>
              <section>
                <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-2">
                  Recent executions ({detail.recentExecutions.length})
                </div>
                <Card>
                  {detail.recentExecutions.length === 0 ? (
                    <div className="p-4 text-sm text-text-muted">No executions recorded.</div>
                  ) : (
                    <div>
                      {detail.recentExecutions.map(exec => (
                        <ExecutionRow key={exec.executionId} exec={exec} />
                      ))}
                    </div>
                  )}
                </Card>
              </section>

              <section>
                <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-2">
                  Open tasks ({detail.openTasks.length})
                </div>
                <Card>
                  {detail.openTasks.length === 0 ? (
                    <div className="p-4 text-sm text-text-muted">No open tasks.</div>
                  ) : (
                    <div>
                      {detail.openTasks.map(task => (
                        <Link
                          key={task.taskId}
                          to={`/tasks/${task.taskId}`}
                          className="flex items-center gap-2 px-3 py-2 text-xs border-b border-border last:border-b-0 hover:bg-surface-2"
                        >
                          <span className="text-text font-mono">#{task.taskId}</span>
                          <span className="text-text-muted">·</span>
                          <span className="text-text">{formatNumber(task.latestResultCount)} rows</span>
                          {task.snoozedUntil && (
                            <Pill tone="info">Snoozed</Pill>
                          )}
                          <span className="ml-auto text-text-muted">
                            {formatRelativeTime(task.createdTime)}
                          </span>
                        </Link>
                      ))}
                    </div>
                  )}
                </Card>
              </section>

              <section>
                <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-2">
                  Recent anomalies ({detail.recentAnomalies.length})
                </div>
                <Card>
                  {detail.recentAnomalies.length === 0 ? (
                    <div className="p-4 text-sm text-text-muted">No anomalies in window.</div>
                  ) : (
                    <div>
                      {detail.recentAnomalies.map(a => (
                        <div
                          key={a.anomalyId}
                          className="flex flex-col gap-1 py-2 px-3 border-b border-border last:border-b-0"
                        >
                          <div className="flex items-center gap-2 text-xs">
                            <Pill tone={SEVERITY_TONE[a.severity] ?? 'neutral'}>{a.severity}</Pill>
                            <span className="text-text" title={formatDateTime(a.detectedTime)}>
                              {formatRelativeTime(a.detectedTime)}
                            </span>
                            <span className="text-text-muted">·</span>
                            <span className="text-text-muted">value {a.currentValue}</span>
                            {a.acknowledged && (
                              <Pill tone="neutral" className="ml-auto">
                                ack
                              </Pill>
                            )}
                          </div>
                          {a.explanation && (
                            <div className="text-2xs text-text-muted">{a.explanation}</div>
                          )}
                        </div>
                      ))}
                    </div>
                  )}
                </Card>
              </section>

              <section className="text-2xs text-text-subtle">
                Cron: <code className="font-mono">{detail.cronExpression}</code> · window:{' '}
                {detail.timeRangeDays}d
              </section>
            </>
          )}
        </div>
      </aside>
    </div>
  );
}
