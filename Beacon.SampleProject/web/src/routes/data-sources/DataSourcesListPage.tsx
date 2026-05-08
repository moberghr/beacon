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
  useDataSourcesQuery,
  useDeleteDataSource,
  type DataSourceEntry,
} from './queries';
import { AddDataSourceDialog } from './AddDataSourceDialog';

const GRID_TEMPLATE = '0.6fr 1.6fr 1fr 1fr 0.7fr 0.7fr 100px 60px';

export default function DataSourcesListPage() {
  const { data, isLoading, isError, error, refetch } = useDataSourcesQuery();
  const deleteMutation = useDeleteDataSource();

  const [deleting, setDeleting] = useState<DataSourceEntry | null>(null);
  const [addOpen, setAddOpen] = useState(false);

  const entries = data?.entries ?? [];

  const columns = useMemo<Column<DataSourceEntry>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name}</span>,
    },
    {
      key: 'type',
      header: 'Type',
      render: r => <span className="pill">{r.dataSourceType}</span>,
    },
    {
      key: 'engine',
      header: 'Engine',
      render: r => r.databaseEngineType
        ? <span className="muted mono">{r.databaseEngineType}</span>
        : <span className="muted">—</span>,
    },
    {
      key: 'queries',
      header: 'Queries',
      render: r => formatNumber(r.queryCount),
    },
    {
      key: 'migrations',
      header: 'Migrations',
      render: r => formatNumber(r.migrationJobsCount),
    },
    {
      key: 'details',
      header: '',
      render: r => (
        <a
          className="btn btn--ghost"
          href={`/beacon/data-sources/${r.id}`}
          onClick={e => e.stopPropagation()}
          title="Open details"
        >
          Details
        </a>
      ),
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <button
          type="button"
          className="btn btn--ghost"
          aria-label={`Delete data source ${r.name}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete data source"
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
      toast.success(`Deleted data source '${deleting.name}'`);
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
        title="Data sources"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} configured</span>
        }
        actions={
          <>
            <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
            <button className="btn btn--primary" type="button" onClick={() => setAddOpen(true)}>
              <Icon.Plus size={14} className="btn__icon" />
              New data source
            </button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load data sources"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <button className="btn btn--primary" type="button" onClick={() => refetch()}>
              Retry
            </button>
          }
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Icon.Database size={20} />}
                title={isLoading ? 'Loading data sources…' : 'No data sources yet'}
                description={isLoading ? '' : 'Connect a database, file, or API endpoint to start running queries.'}
              />
            }
          />
        </div>
      )}

      <AddDataSourceDialog open={addOpen} onClose={() => setAddOpen(false)} />

      <ConfirmDialog
        open={deleting != null}
        title="Delete data source"
        message={
          deleting
            ? <>Delete data source <strong>{deleting.name}</strong>? This will remove all related queries and migrations. This cannot be undone.</>
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
