import { useState } from 'react';
import type { MigrationExecutionDto } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useMigrationExecutionsQuery } from './queries';
import { CreateMigrationJobDialog } from './CreateMigrationJobDialog';

const STATUS_PILL: Record<number, { label: string; cls: string }> = {
  1: { label: 'Queued', cls: 'pill' },
  2: { label: 'Running', cls: 'pill pill--info' },
  3: { label: 'Completed', cls: 'pill pill--ok' },
  4: { label: 'Failed', cls: 'pill pill--crit' },
  5: { label: 'Cancelled', cls: 'pill' },
  6: { label: 'Partial', cls: 'pill pill--warn' },
};

const COLUMNS: Column<MigrationExecutionDto>[] = [
  {
    key: 'started',
    header: 'Started',
    render: r => <span className="muted mono">{formatDateTime(r.startedAt)}</span>,
  },
  {
    key: 'job',
    header: 'Job',
    render: r => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{r.migrationJobName}</div>
        <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>#{r.migrationJobId}{r.isRetry && ' · retry'}</div>
      </div>
    ),
  },
  {
    key: 'status',
    header: 'Status',
    render: r => {
      const map = STATUS_PILL[r.status] ?? { label: String(r.status), cls: 'pill' };
      return <span className={map.cls}>{map.label}</span>;
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
        ? <span style={{ color: 'var(--crit, #c00)' }}>{formatNumber(r.rowsFailed)}</span>
        : <span className="muted">—</span>,
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
    <div className="page">
      <PageHeader
        title="Migration history"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(rows.length)} of {formatNumber(data?.totalCount ?? 0)}</span>
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
          title="Failed to load migration history"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={COLUMNS}
            rows={rows}
            rowKey={r => r.id}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Icon.ArrowsLR size={20} />}
                title={isLoading ? 'Loading executions…' : 'No migration executions yet'}
                description={isLoading ? '' : 'Run a migration job to see its history here.'}
              />
            }
          />
        </div>
      )}
    </div>
  );
}
