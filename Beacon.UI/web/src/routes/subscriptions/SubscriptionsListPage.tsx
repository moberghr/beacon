import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, Inbox, Plus, RefreshCw, X } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Button, Card, Input, PageHeader, Pill } from '@/components/beacon';
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
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const { data, isLoading, isError, error, refetch } = useSubscriptionsQuery(search);
  const deleteMutation = useDeleteSubscription();

  const [adding, setAdding] = useState(false);
  const [deleting, setDeleting] = useState<SubscriptionEntry | null>(null);

  const entries = data?.entries ?? [];

  const columns = useMemo<Column<SubscriptionEntry>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="text-text-muted mono">{r.id}</span> },
    { key: 'queryId', header: 'Query', render: r => <span className="text-text-muted mono">{r.queryId}</span> },
    {
      key: 'queryName',
      header: 'Query name',
      render: r => <span className="font-semibold text-text">{r.queryName}</span>,
    },
    {
      key: 'cron',
      header: 'Schedule',
      render: r => <span className="mono text-xs">{r.cronExpression}</span>,
    },
    {
      key: 'recipients',
      header: 'Recipients',
      render: r => {
        if (r.recipientCount === 0) {
          return <span className="text-text-muted">—</span>;
        }
        const head = r.recipientNames.slice(0, 3).join(', ');
        const more = r.recipientCount - 3;
        return (
          <span className="inline-flex items-center gap-1.5">
            <Pill>{r.recipientCount}</Pill>
            <span>{head}</span>
            {more > 0 && <span className="text-text-muted">+{more}</span>}
          </span>
        );
      },
    },
    {
      key: 'source',
      header: 'Source',
      render: r => r.aiActorName
        ? <Pill tone="ok">{r.aiActorName}</Pill>
        : <span className="text-text-muted">Direct</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          size="sm"
          aria-label={`Delete subscription ${r.id}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete subscription"
          icon={<X />}
        />
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
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Delivery"
        emphasis="Subscriptions"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <Button icon={<RefreshCw />} onClick={() => refetch()} disabled={isLoading}>
              Refresh
            </Button>
            <Button variant="primary" icon={<Plus />} onClick={() => setAdding(true)}>
              New subscription
            </Button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load subscriptions"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => refetch()}>Retry</Button>
          }
        />
      )}

      {!isError && (
        <>
          <Card className="p-3">
            <Input
              type="search"
              placeholder="Search by query name…"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </Card>

          <DataTable
            columns={columns}
            rows={entries}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={r => navigate(`/subscriptions/${r.id}`)}
            empty={
              <EmptyState
                icon={<Inbox />}
                title={isLoading ? 'Loading subscriptions…' : 'No subscriptions yet'}
                description={isLoading ? '' : 'Schedule a query and route its results to recipients.'}
              />
            }
          />
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
