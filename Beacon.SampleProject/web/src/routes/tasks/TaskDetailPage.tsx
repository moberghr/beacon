import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { useTaskDetailQuery } from './queries';
import { ResolveTaskDialog } from './ResolveTaskDialog';

export default function TaskDetailPage() {
  const params = useParams<{ id: string }>();
  const id = Number(params.id);
  const navigate = useNavigate();
  const [resolveOpen, setResolveOpen] = useState(false);

  const { data, isLoading, isError, error } = useTaskDetailQuery(Number.isFinite(id) ? id : undefined);

  if (!Number.isFinite(id)) {
    return (
      <div className="page">
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid task id" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load task"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  return (
    <div className="page">
      <PageHeader
        title={data ? `Task #${data.id}` : isLoading ? 'Loading…' : 'Task'}
        sub={
          data
            ? <span className="muted">Created {formatRelativeTime(data.createdAt)} · {data.resolved ? 'Resolved' : 'Open'}</span>
            : null
        }
        actions={
          data && !data.resolved ? (
            <button type="button" className="btn btn--primary" onClick={() => setResolveOpen(true)}>
              <Icon.Check size={14} className="btn__icon" />
              Resolve
            </button>
          ) : (
            <Link to="/tasks" className="btn">
              <span style={{ display: 'inline-block', transform: 'rotate(180deg)', marginRight: 6 }}>
                <Icon.Chevron size={14} />
              </span>
              Back to tasks
            </Link>
          )
        }
      />

      {isLoading && <div className="muted">Loading…</div>}

      {data && (
        <div style={{ display: 'grid', gap: 16 }}>
          <div className="card">
            <div className="card__head">
              <div className="card__title">Overview</div>
            </div>
            <div className="card__body">
              <div className="kv">
                <div className="kv__row">
                  <div className="kv__label">Subscription</div>
                  <div className="kv__value">{data.subscriptionName}</div>
                </div>
                <div className="kv__row">
                  <div className="kv__label">Query</div>
                  <div className="kv__value">
                    <Link to={`/queries/${data.queryId}/versions`}>{data.queryName}</Link>
                  </div>
                </div>
                <div className="kv__row">
                  <div className="kv__label">Latest result count</div>
                  <div className="kv__value">{formatNumber(data.latestResultCount)}</div>
                </div>
                <div className="kv__row">
                  <div className="kv__label">Notifications</div>
                  <div className="kv__value">{formatNumber(data.notificationCount)}</div>
                </div>
                <div className="kv__row">
                  <div className="kv__label">Last notification</div>
                  <div className="kv__value">
                    {data.lastNotificationAt ? formatDateTime(data.lastNotificationAt) : <span className="muted">Never</span>}
                  </div>
                </div>
                <div className="kv__row">
                  <div className="kv__label">Source</div>
                  <div className="kv__value">
                    {data.aiActorName ?? <span className="muted">User</span>}
                  </div>
                </div>
              </div>
            </div>
          </div>

          {data.resolved && (
            <div className="card">
              <div className="card__head">
                <div className="card__title">Resolution</div>
              </div>
              <div className="card__body">
                <div className="kv">
                  <div className="kv__row">
                    <div className="kv__label">Resolved at</div>
                    <div className="kv__value">{data.resolvedAt ? formatDateTime(data.resolvedAt) : '—'}</div>
                  </div>
                  <div className="kv__row">
                    <div className="kv__label">Resolved by</div>
                    <div className="kv__value">{data.resolvedByUserName ?? <span className="muted">System</span>}</div>
                  </div>
                </div>
                {data.resolutionNotes && (
                  <div style={{ marginTop: 12 }}>
                    <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Notes</div>
                    <div style={{ whiteSpace: 'pre-wrap', fontSize: 13 }}>{data.resolutionNotes}</div>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      <ResolveTaskDialog
        open={resolveOpen}
        taskId={data?.id ?? null}
        onClose={() => {
          setResolveOpen(false);
          // Stay on the page; query invalidation triggers refetch.
          if (data) navigate(`/tasks/${data.id}`, { replace: true });
        }}
      />
    </div>
  );
}
