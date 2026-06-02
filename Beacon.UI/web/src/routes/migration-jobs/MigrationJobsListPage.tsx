import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, ArrowLeftRight, Plus, RefreshCw, X } from 'lucide-react';
import { PageHeader, Button, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { describeError } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import {
  useMigrationJobsQuery,
  useDeleteMigrationJob,
  type MigrationJobListItem,
} from './queries';
import { MIGRATION_MODE_LABEL, type MigrationModeId } from '@/routes/migration-history/queries';

const GRID_TEMPLATE = '0.5fr 1.4fr 1.4fr 1fr 0.7fr 0.6fr 1.1fr 60px';

export default function MigrationJobsListPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useMigrationJobsQuery();
  const deleteMutation = useDeleteMigrationJob();
  const [deleting, setDeleting] = useState<MigrationJobListItem | null>(null);

  const jobs = data?.jobs ?? [];

  const columns = useMemo<Column<MigrationJobListItem>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="mono text-text-muted">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span className="font-semibold text-text">{r.name}</span>,
    },
    {
      key: 'flow',
      header: 'Source → destination',
      render: r => (
        <span className="text-text-muted text-xs">
          {r.dataSourceName} → {r.destinationDataSourceName} ({r.destinationTable})
        </span>
      ),
    },
    {
      key: 'mode',
      header: 'Mode',
      render: r => <Pill>{MIGRATION_MODE_LABEL[r.mode as MigrationModeId] ?? r.mode}</Pill>,
    },
    {
      key: 'enabled',
      header: 'Enabled',
      render: r => r.isEnabled
        ? <Pill tone="ok">On</Pill>
        : <span className="text-text-muted">Off</span>,
    },
    {
      key: 'schedule',
      header: 'Schedule',
      render: r => r.schedule ? <span className="mono text-xs">{r.schedule}</span> : <span className="text-text-muted">—</span>,
    },
    {
      key: 'created',
      header: 'Created',
      render: r => <span className="mono text-text-muted">{formatDateTime(r.createdTime)}</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          size="sm"
          aria-label={`Delete ${r.name}`}
          onClick={e => { e.stopPropagation(); setDeleting(r); }}
          title="Delete migration job"
          icon={<X />}
        />
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
      toast.error(describeError(e, 'Failed to delete migration job'));
    }
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Migrations"
        prefix="Orchestrating"
        emphasis="migration jobs"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(jobs.length)} job(s)</span>
        }
        actions={
          <>
            <Link to="/migration-history"><Button>History</Button></Link>
            <Button type="button" onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            <Link to="/migration-jobs/new">
              <Button variant="primary" icon={<Plus />}>New job</Button>
            </Link>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load migration jobs"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={jobs}
          rowKey={r => r.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={r => navigate(`/migration-jobs/${r.id}`)}
          empty={
            <EmptyState
              icon={<ArrowLeftRight />}
              title={isLoading ? 'Loading jobs…' : 'No migration jobs yet'}
              description={isLoading ? '' : 'Create your first job above.'}
            />
          }
        />
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
