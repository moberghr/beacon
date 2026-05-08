import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ApiError } from '@/lib/api';
import { formatNumber } from '@/lib/format';
import {
  useDeleteSubscription,
  useSubscriptionsQuery,
  type SubscriptionEntry,
} from './queries';
import { AddSubscriptionDialog } from './AddSubscriptionDialog';

const GRID_TEMPLATE = '0.6fr 0.6fr 1.6fr 1.4fr 1.6fr 0.8fr 60px';

export default function SubscriptionsListPage() {
  const [search, setSearch] = useState('');
  const { data, isLoading, isError, error, refetch } = useSubscriptionsQuery(search);
  const deleteMutation = useDeleteSubscription();

  const [adding, setAdding] = useState(false);
  const [deleting, setDeleting] = useState<SubscriptionEntry | null>(null);

  const entries = data?.entries ?? [];

  const columns = useMemo<Column<SubscriptionEntry>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.id}</span> },
    { key: 'queryId', header: 'Query', render: r => <span className="muted mono">{r.queryId}</span> },
    {
      key: 'queryName',
      header: 'Query name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.queryName}</span>,
    },
    {
      key: 'cron',
      header: 'Schedule',
      render: r => <span className="mono" style={{ fontSize: 12 }}>{r.cronExpression}</span>,
    },
    {
      key: 'recipients',
      header: 'Recipients',
      render: r => {
        if (r.recipientCount === 0) {
          return <span className="muted">—</span>;
        }
        const head = r.recipientNames.slice(0, 3).join(', ');
        const more = r.recipientCount - 3;
        return (
          <span>
            <span className="pill" style={{ marginRight: 6 }}>{r.recipientCount}</span>
            {head}
            {more > 0 && <span className="muted"> +{more}</span>}
          </span>
        );
      },
    },
    {
      key: 'source',
      header: 'Source',
      render: r => r.aiActorName
        ? <span className="pill pill--ok">{r.aiActorName}</span>
        : <span className="muted">Direct</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <button
          type="button"
          className="btn btn--ghost"
          aria-label={`Delete subscription ${r.id}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete subscription"
        >
          <Icon.X size={14} />
        </button>
      ),
    },
  ], []);

  const onConfirmDelete = async () => {
    if (deleting == null) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success(`Deleted subscription #${deleting.id}`);
      setDeleting(null);
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Delete failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  };

  return (
    <div className="page">
      <PageHeader
        title="Subscriptions"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
            <button
              className="btn btn--primary"
              type="button"
              onClick={() => setAdding(true)}
            >
              <Icon.Plus size={14} className="btn__icon" />
              New subscription
            </button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load subscriptions"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <button className="btn btn--primary" type="button" onClick={() => refetch()}>
              Retry
            </button>
          }
        />
      )}

      {!isError && (
        <>
          <div className="card" style={{ padding: 12, marginBottom: 12 }}>
            <input
              type="search"
              className="q-input"
              placeholder="Search by query name…"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>

          <div className="card" style={{ padding: 0 }}>
            <DataTable
              columns={columns}
              rows={entries}
              rowKey={r => r.id}
              gridTemplate={GRID_TEMPLATE}
              empty={
                <EmptyState
                  icon={<Icon.Inbox size={20} />}
                  title={isLoading ? 'Loading subscriptions…' : 'No subscriptions yet'}
                  description={isLoading ? '' : 'Schedule a query and route its results to recipients.'}
                />
              }
            />
          </div>
        </>
      )}

      <AddSubscriptionDialog open={adding} onClose={() => setAdding(false)} />

      <ConfirmDialog
        open={deleting != null}
        title="Delete subscription"
        message={
          deleting
            ? <>Delete subscription <strong>#{deleting.id}</strong> for <strong>{deleting.queryName}</strong>? This cannot be undone.</>
            : ''
        }
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={onConfirmDelete}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
