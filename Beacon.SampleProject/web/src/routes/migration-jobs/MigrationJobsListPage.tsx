import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ApiError } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import { CreateMigrationJobDialog } from '@/routes/migration-history/CreateMigrationJobDialog';
import {
  useMigrationJobsQuery,
  useDeleteMigrationJob,
  MIGRATION_MODE_LABEL,
  type MigrationJobListItem,
} from './queries';

const GRID_TEMPLATE = '0.5fr 1.4fr 1.4fr 1fr 0.7fr 0.6fr 1.1fr 60px';

export default function MigrationJobsListPage() {
  const { data, isLoading, isError, error, refetch } = useMigrationJobsQuery();
  const deleteMutation = useDeleteMigrationJob();
  const [createOpen, setCreateOpen] = useState(false);
  const [deleting, setDeleting] = useState<MigrationJobListItem | null>(null);

  const jobs = data?.jobs ?? [];

  const columns = useMemo<Column<MigrationJobListItem>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name}</span>,
    },
    {
      key: 'flow',
      header: 'Source → destination',
      render: r => (
        <span className="muted" style={{ fontSize: 12 }}>
          {r.dataSourceName} → {r.destinationDataSourceName} ({r.destinationTable})
        </span>
      ),
    },
    {
      key: 'mode',
      header: 'Mode',
      render: r => <span className="pill">{MIGRATION_MODE_LABEL[r.mode] ?? r.mode}</span>,
    },
    {
      key: 'enabled',
      header: 'Enabled',
      render: r => r.isEnabled
        ? <span className="pill pill--ok">On</span>
        : <span className="muted">Off</span>,
    },
    {
      key: 'schedule',
      header: 'Schedule',
      render: r => r.schedule ? <span className="mono" style={{ fontSize: 12 }}>{r.schedule}</span> : <span className="muted">—</span>,
    },
    {
      key: 'created',
      header: 'Created',
      render: r => <span className="muted mono">{formatDateTime(r.createdTime)}</span>,
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
          title="Delete migration job"
        >
          <Icon.X size={14} />
        </button>
      ),
    },
  ], []);

  async function handleDelete() {
    if (!deleting) return;
    try {
      const result = await deleteMutation.mutateAsync({ id: deleting.id });
      if (!result?.success) {
        toast.error(result?.errorMessage || 'Failed to delete migration job');
        return;
      }
      toast.success('Migration job deleted');
      setDeleting(null);
    } catch (e) {
      toast.error(e instanceof ApiError ? e.body || e.message : 'Failed to delete migration job');
    }
  }

  return (
    <div className="page">
      <PageHeader
        title="Migration jobs"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(jobs.length)} job(s)</span>
        }
        actions={
          <>
            <Link className="btn" to="/migration-history">History</Link>
            <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
            <button
              className="btn btn--primary"
              type="button"
              onClick={() => setCreateOpen(true)}
            >
              + New job
            </button>
          </>
        }
      />

      <CreateMigrationJobDialog open={createOpen} onClose={() => setCreateOpen(false)} />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load migration jobs"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={jobs}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Icon.ArrowsLR size={20} />}
                title={isLoading ? 'Loading jobs…' : 'No migration jobs yet'}
                description={isLoading ? '' : 'Create your first job above.'}
              />
            }
          />
        </div>
      )}

      <ConfirmDialog
        open={deleting !== null}
        title="Delete migration job"
        message={deleting ? <>Delete <strong>{deleting.name}</strong>? Execution history will be retained.</> : null}
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onCancel={() => setDeleting(null)}
        onConfirm={handleDelete}
      />
    </div>
  );
}
