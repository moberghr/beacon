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
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name}</span>,
    },
    {
      key: 'destination',
      header: 'Destination',
      render: r => <span className="mono" style={{ fontSize: 12 }}>{r.destination}</span>,
    },
    {
      key: 'type',
      header: 'Type',
      render: r => <span className="pill">{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</span>,
    },
    {
      key: 'description',
      header: 'Description',
      render: r => r.description
        ? <span className="muted">{r.description}</span>
        : <span className="muted">—</span>,
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
        <button
          type="button"
          className="btn btn--ghost"
          aria-label={`Delete ${r.name}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete recipient"
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
    <div className="page">
      <PageHeader
        title="Recipients"
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
              onClick={() => setEditing(null)}
            >
              <Icon.Plus size={14} className="btn__icon" />
              Add recipient
            </button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load recipients"
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
              placeholder="Search by name, destination, or description"
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </div>

          <div className="card" style={{ padding: 0 }}>
            <DataTable
              columns={columns}
              rows={filtered}
              rowKey={r => r.id}
              gridTemplate={GRID_TEMPLATE}
              onRowClick={r => setEditing(r)}
              empty={
                <EmptyState
                  icon={<Icon.Users size={20} />}
                  title={isLoading ? 'Loading recipients…' : 'No recipients yet'}
                  description={isLoading ? '' : 'Add a recipient so subscriptions have somewhere to send alerts.'}
                />
              }
            />
          </div>
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
