import { useState } from 'react';
import type { MigrationExecutionDto } from '@/api/generated/beacon-api';
import { AlertTriangle, ArrowLeftRight, Plus, RefreshCw } from 'lucide-react';
import { PageHeader, Button, Pill, type PillProps } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useMigrationExecutionsQuery } from './queries';
import { CreateMigrationJobDialog } from './CreateMigrationJobDialog';

const STATUS_PILL: Record<number, { label: string; tone: PillProps['tone'] }> = {
  1: { label: 'Queued', tone: 'neutral' },
  2: { label: 'Running', tone: 'info' },
  3: { label: 'Completed', tone: 'ok' },
  4: { label: 'Failed', tone: 'crit' },
  5: { label: 'Cancelled', tone: 'neutral' },
  6: { label: 'Partial', tone: 'warn' },
};

const COLUMNS: Column<MigrationExecutionDto>[] = [
  {
    key: 'started',
    header: 'Started',
    render: r => <span className="mono text-text-muted">{formatDateTime(r.startedAt)}</span>,
  },
  {
    key: 'job',
    header: 'Job',
    render: r => (
      <div>
        <div className="font-semibold text-text">{r.migrationJobName}</div>
        <div className="text-text-muted text-xs mt-0.5">#{r.migrationJobId}{r.isRetry && ' · retry'}</div>
      </div>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    render: r => {
      const map = STATUS_PILL[r.status] ?? { label: String(r.status), tone: 'neutral' as const };
      return <Pill tone={map.tone}>{map.label}</Pill>;
    },
  },
  {
    key: 'rows',
    header: 'Rows',
    render: r => (
      <span className="mono">
        {formatNumber(r.destinationRowsWritten)} / {formatNumber(r.sourceRowsRead)}
      </span>
    ),
  },
  {
    key: 'failed',
    header: 'Failed',
    render: r =>
      r.rowsFailed > 0
        ? <span className="text-crit">{formatNumber(r.rowsFailed)}</span>
        : <span className="text-text-muted">—</span>,
  },
  {
    key: 'rps',
    header: 'Rows/s',
    render: r => formatNumber(Math.round(r.rowsPerSecond)),
  },
];

const GRID_TEMPLATE = '1.4fr 2fr 0.9fr 1.1fr 0.7fr 0.7fr';

export default function MigrationHistoryPage() {
  const { data, isLoading, isError, error, refetch } = useMigrationExecutionsQuery();
  const [createOpen, setCreateOpen] = useState(false);
  const rows = data?.executions ?? [];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Migrations"
        prefix="Tracing"
        emphasis="migration history"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(rows.length)} of {formatNumber(data?.totalCount ?? 0)}</span>
        }
        actions={
          <>
            <Button type="button" onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            <Button
              variant="primary"
              type="button"
              onClick={() => setCreateOpen(true)}
              icon={<Plus />}
            >
              New job
            </Button>
          </>
        }
      />

      <CreateMigrationJobDialog open={createOpen} onClose={() => setCreateOpen(false)} />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load migration history"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={COLUMNS}
          rows={rows}
          rowKey={r => r.id}
          gridTemplate={GRID_TEMPLATE}
          empty={
            <EmptyState
              icon={<ArrowLeftRight />}
              title={isLoading ? 'Loading executions…' : 'No migration executions yet'}
              description={isLoading ? '' : 'Run a migration job to see its history here.'}
            />
          }
        />
      )}
    </div>
  );
}
