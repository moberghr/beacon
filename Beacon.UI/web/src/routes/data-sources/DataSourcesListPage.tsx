import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, Database, Plus, RefreshCw, X } from 'lucide-react';
import { PageHeader } from '@/components/beacon';
import { Button, Pill } from '@/components/beacon';
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
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useDataSourcesQuery();
  const deleteMutation = useDeleteDataSource();

  const [deleting, setDeleting] = useState<DataSourceEntry | null>(null);
  const [addOpen, setAddOpen] = useState(false);

  const entries = data?.entries ?? [];

  const columns = useMemo<Column<DataSourceEntry>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="mono text-text-muted">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span className="font-semibold text-text">{r.name}</span>,
    },
    {
      key: 'type',
      header: 'Type',
      render: r => <Pill>{r.dataSourceType}</Pill>,
    },
    {
      key: 'engine',
      header: 'Engine',
      render: r => r.databaseEngineType
        ? <span className="mono text-text-muted">{r.databaseEngineType}</span>
        : <span className="text-text-muted">—</span>,
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
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          size="sm"
          aria-label={`Delete data source ${r.name}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete data source"
          icon={<X />}
        />
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
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Data"
        prefix="Connected"
        emphasis="data sources"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} configured</span>
        }
        actions={
          <>
            <Button type="button" onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            <Button variant="primary" type="button" onClick={() => setAddOpen(true)} icon={<Plus />}>
              New data source
            </Button>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load data sources"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" type="button" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={entries}
          rowKey={r => r.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={r => navigate(`/data-sources/${r.id}`)}
          empty={
            <EmptyState
              icon={<Database />}
              title={isLoading ? 'Loading data sources…' : 'No data sources yet'}
              description={isLoading ? '' : 'Connect a database, file, or API endpoint to start running queries.'}
            />
          }
        />
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
