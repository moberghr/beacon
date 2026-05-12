import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { AlertTriangle, Plus, RefreshCw, Users, X } from 'lucide-react';
import { Button, PageHeader, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ApiError } from '@/lib/api';
import { formatNumber } from '@/lib/format';
import {
  NOTIFICATION_TYPE_LABEL,
  useDeleteRecipient,
  useRecipientsQuery,
  type RecipientEntry,
} from './queries';
import { RecipientDialog } from './RecipientDialog';

const GRID_TEMPLATE = '0.6fr 1.4fr 1.6fr 0.9fr 1.6fr 0.6fr 60px';

export default function RecipientsListPage() {
  const { data, isLoading, isError, error, refetch } = useRecipientsQuery();
  const deleteMutation = useDeleteRecipient();

  const [editing, setEditing] = useState<RecipientEntry | null | undefined>(undefined); // undefined = closed
  const [deleting, setDeleting] = useState<RecipientEntry | null>(null);
  const [search, setSearch] = useState('');

  const entries = data?.entries ?? [];
  const filtered = useMemo(() => {
    if (!search.trim()) return entries;
    const q = search.trim().toLowerCase();
    return entries.filter(r =>
      r.name.toLowerCase().includes(q)
      || r.destination.toLowerCase().includes(q)
      || (r.description ?? '').toLowerCase().includes(q));
  }, [entries, search]);

  const columns = useMemo<Column<RecipientEntry>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="text-text-muted mono">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span className="font-semibold text-text">{r.name}</span>,
    },
    {
      key: 'destination',
      header: 'Destination',
      render: r => <span className="mono text-xs">{r.destination}</span>,
    },
    {
      key: 'type',
      header: 'Type',
      render: r => <Pill>{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</Pill>,
    },
    {
      key: 'description',
      header: 'Description',
      render: r => r.description
        ? <span className="text-text-muted">{r.description}</span>
        : <span className="text-text-muted">—</span>,
    },
    {
      key: 'subs',
      header: 'Subs',
      render: r => formatNumber(r.subscriptionCount),
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          aria-label={`Delete ${r.name}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete recipient"
          icon={<X />}
        />
      ),
    },
  ], []);

  const onConfirmDelete = async () => {
    if (deleting == null) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success(`Deleted recipient '${deleting.name}'`);
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
        eyebrow="Notifications"
        emphasis="Recipients"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            <Button variant="primary" onClick={() => setEditing(null)} icon={<Plus />}>
              Add recipient
            </Button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load recipients"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      )}

      {!isError && (
        <>
          <div className="bg-surface border border-border rounded-md shadow-sm p-3">
            <input
              type="search"
              className="w-full bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm placeholder:text-text-subtle focus:border-brand-500 focus:outline-none focus:shadow-ring"
              placeholder="Search by name, destination, or description"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>

          <DataTable
            columns={columns}
            rows={filtered}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={r => setEditing(r)}
            empty={
              <EmptyState
                icon={<Users />}
                title={isLoading ? 'Loading recipients…' : 'No recipients yet'}
                description={isLoading ? '' : 'Add a recipient so subscriptions have somewhere to send alerts.'}
              />
            }
          />
        </>
      )}

      <RecipientDialog
        open={editing !== undefined}
        recipient={editing ?? null}
        onClose={() => setEditing(undefined)}
      />

      <ConfirmDialog
        open={deleting != null}
        title="Delete recipient"
        message={
          deleting
            ? <>Delete recipient <strong>{deleting.name}</strong>? This cannot be undone.</>
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
