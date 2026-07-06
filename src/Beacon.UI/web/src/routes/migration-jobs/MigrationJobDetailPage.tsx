import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { AlertTriangle, ArrowLeftRight, Layers, Play, RefreshCw, X } from 'lucide-react';
import { beaconApi } from '@/api/client';
import { PageHeader, Button, Card, CardBody, KPI, KPIGrid, Pill } from '@/components/beacon';
import { Tabs, type TabDef } from '@/components/Tabs';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { unwrap } from '@/lib/api';
import { MigrationMode, MigrationStatus } from '@/lib/enums';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import {
  useMigrationJobsQuery,
  useDeleteMigrationJob,
  useRunMigrationJob,
} from './queries';
import {
  MIGRATION_MODE_LABEL,
  type GetMigrationExecutionsResult,
  type MigrationExecutionEntry,
} from '@/routes/migration-history/queries';

type TabKey = 'overview' | 'executions';

const EXECUTION_STATUS_LABEL: Record<MigrationStatus, string> = {
  [MigrationStatus.Queued]: 'Queued',
  [MigrationStatus.Running]: 'Running',
  [MigrationStatus.Completed]: 'Completed',
  [MigrationStatus.Failed]: 'Failed',
  [MigrationStatus.Cancelled]: 'Cancelled',
  [MigrationStatus.PartialSuccess]: 'Partial success',
};

function useJobExecutionsQuery(jobId: number | null) {
  return useQuery({
    queryKey: ['migration-executions', jobId] as const,
    queryFn: async () =>
      unwrap<GetMigrationExecutionsResult>(
        await beaconApi().getMigrationExecutions(jobId!, undefined, undefined, undefined, 0, 50),
      ),
    enabled: jobId != null && Number.isFinite(jobId),
  });
}

export default function MigrationJobDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;

  const listQuery = useMigrationJobsQuery();
  const executions = useJobExecutionsQuery(Number.isFinite(numericId) ? numericId : null);
  const deleteMutation = useDeleteMigrationJob();
  const runMutation = useRunMigrationJob();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [tab, setTab] = useState<TabKey>('overview');

  const job = useMemo(
    () => listQuery.data?.jobs.find(j => j.id === numericId) ?? null,
    [listQuery.data, numericId],
  );

  if (Number.isNaN(numericId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="pulse" emphasis="Migration job" />
        <EmptyState icon={<AlertTriangle />} title="Invalid job id" description={String(id)} />
      </div>
    );
  }

  if (listQuery.isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="pulse" emphasis="Migration job" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (!job) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="pulse"
          emphasis={`Migration job #${numericId}`}
          sub={<Link to="/migration-jobs" className="text-text-muted">← back to migration jobs</Link>}
        />
        <EmptyState icon={<ArrowLeftRight />} title="Migration job not found" />
      </div>
    );
  }

  const handleRun = async () => {
    try {
      const result = await runMutation.mutateAsync({ id: job.id });
      if (result.status === MigrationStatus.Failed) {
        toast.error(result.errorMessage ?? 'Migration failed');
        return;
      }
      if (result.status === MigrationStatus.PartialSuccess) {
        toast.warning(
          `Migration finished with partial success — ${formatNumber(result.sourceRowsRead)} read, ${formatNumber(result.destinationRowsWritten)} written, ${formatNumber(result.rowsFailed)} failed`,
        );
        return;
      }
      if (result.status === MigrationStatus.Completed) {
        toast.success(
          `Migration succeeded — ${formatNumber(result.sourceRowsRead)} read, ${formatNumber(result.destinationRowsWritten)} written`,
        );
        return;
      }
      toast.success(`Migration finished with status ${EXECUTION_STATUS_LABEL[result.status] ?? result.status}`);
    } catch {
      // useRunMigrationJob (createSimpleMutation) already toasts the error.
    }
  };

  const handleDelete = async () => {
    try {
      const result = await deleteMutation.mutateAsync({ id: job.id });
      if (!result.success) {
        toast.error(result.errorMessage ?? 'Failed to delete migration job');
        return;
      }
      toast.success('Migration job deleted');
      navigate('/migration-jobs');
    } catch {
      // useDeleteMigrationJob (createSimpleMutation) already toasts the error.
    }
  };

  const execs = executions.data?.executions ?? [];
  const lastExec = execs[0];
  const successCount = execs.filter(e => e.status === MigrationStatus.Completed).length;
  const failedCount = execs.filter(e => e.status === MigrationStatus.Failed).length;

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Layers className="size-3.5" /> Overview</> },
    { key: 'executions', label: <><RefreshCw className="size-3.5" /> Executions</>, count: execs.length },
  ];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow={<>Migration jobs <span className="eyebrow-sep">/</span> <span className="mono">#{job.id}</span></>}
        emphasis={job.name}
        sub={
          <>
            <Link to="/migration-jobs" className="text-text-muted">Migration jobs</Link>
            <span className="mx-1.5">/</span>
            <span className="mono">#{job.id}</span>
          </>
        }
        actions={
          <>
            <Button
              type="button"
              onClick={() => { listQuery.refetch(); executions.refetch(); }}
              icon={<RefreshCw />}
            >
              Refresh
            </Button>
            <Button
              variant="primary"
              type="button"
              onClick={handleRun}
              disabled={!job.isEnabled || runMutation.isPending}
              icon={<Play />}
              title={!job.isEnabled ? 'Enable the job to run it' : 'Run this migration now'}
            >
              {runMutation.isPending ? 'Running…' : 'Run now'}
            </Button>
            <Button
              variant="danger"
              type="button"
              onClick={() => setConfirmDelete(true)}
              icon={<X />}
            >
              Delete
            </Button>
          </>
        }
      />

      <KPIGrid>
        <KPI
          dot={job.isEnabled ? 'ok' : 'warn'}
          label="Status"
          value={job.isEnabled ? 'Enabled' : 'Paused'}
          sub={MIGRATION_MODE_LABEL[job.mode as MigrationMode] ?? `mode ${job.mode}`}
        />
        <KPI
          dot="brand"
          label="Last run"
          value={lastExec ? formatRelativeTime(lastExec.startedAt) : '—'}
          sub={lastExec ? EXECUTION_STATUS_LABEL[lastExec.status] ?? '—' : 'never'}
        />
        <KPI dot="ok" label="Successes" value={formatNumber(successCount)} sub={`of last ${execs.length}`} />
        <KPI dot={failedCount > 0 ? 'warn' : 'info'} label="Failures" value={formatNumber(failedCount)} sub={`of last ${execs.length}`} />
      </KPIGrid>

      <Card>
        <Tabs tabs={tabs} active={tab} onChange={setTab} />

        {tab === 'overview' && <OverviewTab job={job} />}
        {tab === 'executions' && (
          <ExecutionsTab
            executions={execs}
            isLoading={executions.isLoading}
            isError={executions.isError}
            error={executions.error}
          />
        )}
      </Card>

      <ConfirmDialog
        open={confirmDelete}
        title="Delete migration job"
        message={<>Delete <strong>{job.name}</strong>? Execution history will be retained.</>}
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={handleDelete}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}

interface OverviewJob {
  id: number;
  name: string;
  description: string;
  dataSourceName: string;
  destinationDataSourceName: string;
  destinationTable: string;
  mode: number;
  isEnabled: boolean;
  schedule: string | null;
  createdTime: string;
}

function OverviewTab({ job }: { job: OverviewJob }) {
  return (
    <CardBody>
      {job.description && (
        <div className="text-text-muted text-[13px] mb-4">
          {job.description}
        </div>
      )}
      <dl className="grid grid-cols-[200px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
        <dt className="text-text-muted">Source</dt>
        <dd><span className="font-semibold">{job.dataSourceName}</span></dd>
        <dt className="text-text-muted">Destination</dt>
        <dd>
          <span className="font-semibold">{job.destinationDataSourceName}</span>
          <span className="text-text-muted"> · </span>
          <span className="mono">{job.destinationTable}</span>
        </dd>
        <dt className="text-text-muted">Mode</dt>
        <dd><Pill>{MIGRATION_MODE_LABEL[job.mode as MigrationMode] ?? job.mode}</Pill></dd>
        <dt className="text-text-muted">Schedule</dt>
        <dd>
          {job.schedule
            ? <span className="mono">{job.schedule}</span>
            : <span className="text-text-muted">manual only</span>}
        </dd>
        <dt className="text-text-muted">Enabled</dt>
        <dd>{job.isEnabled
          ? <Pill tone="ok">On</Pill>
          : <span className="text-text-muted">Off</span>}</dd>
        <dt className="text-text-muted">Created</dt>
        <dd className="mono">{formatDateTime(job.createdTime)}</dd>
      </dl>
    </CardBody>
  );
}

const EXEC_GRID = '0.6fr 1fr 0.8fr 1fr 1fr 0.7fr 0.8fr';

function ExecutionsTab({ executions, isLoading, isError, error }: {
  executions: MigrationExecutionEntry[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
}) {
  const columns: Column<MigrationExecutionEntry>[] = [
    { key: 'id', header: 'Id', render: r => <span className="mono text-text-muted">#{r.id}</span> },
    {
      key: 'started',
      header: 'Started',
      render: r => (
        <span title={formatDateTime(r.startedAt)}>
          {formatRelativeTime(r.startedAt)}
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      render: r => {
        const label = EXECUTION_STATUS_LABEL[r.status] ?? `status ${r.status}`;
        if (r.status === MigrationStatus.Completed) return <Pill tone="ok">{label}</Pill>;
        if (r.status === MigrationStatus.Failed) return <Pill tone="crit">{label}</Pill>;
        if (r.status === MigrationStatus.PartialSuccess) return <Pill tone="warn">{label}</Pill>;
        return <Pill>{label}</Pill>;
      },
    },
    {
      key: 'duration',
      header: 'Duration',
      render: r => <span className="mono">{r.executionDuration}</span>,
    },
    {
      key: 'rows',
      header: 'Rows',
      render: r => (
        <span className="text-text-muted text-xs">
          {formatNumber(r.sourceRowsRead)} read · {formatNumber(r.destinationRowsWritten)} written
          {r.rowsFailed > 0 && <> · {formatNumber(r.rowsFailed)} failed</>}
        </span>
      ),
    },
    {
      key: 'retry',
      header: 'Retry',
      render: r => r.isRetry ? <span className="text-text-muted">#{r.retryAttempt}</span> : <span className="text-text-muted">—</span>,
    },
    {
      key: 'error',
      header: 'Error',
      render: r => r.errorMessage
        ? <span className="text-text-muted text-xs" title={r.errorMessage}>
            {r.errorMessage.length > 50 ? `${r.errorMessage.slice(0, 50)}…` : r.errorMessage}
          </span>
        : <span className="text-text-muted">—</span>,
    },
  ];

  if (isLoading) {
    return <CardBody><span className="text-text-muted">Loading executions…</span></CardBody>;
  }

  if (isError) {
    return (
      <CardBody>
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load executions"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </CardBody>
    );
  }

  return (
    <CardBody flush>
      <DataTable
        columns={columns}
        rows={executions}
        rowKey={r => r.id}
        gridTemplate={EXEC_GRID}
        empty={
          <EmptyState
            icon={<RefreshCw />}
            title="No executions yet"
            description="Runs will appear here after the first execution."
          />
        }
      />
    </CardBody>
  );
}
