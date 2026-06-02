import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Check, RefreshCw } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, Card, PageHeader, Pill } from '@/components/beacon';
import { cn } from '@/lib/cn';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { useTasksQuery, type TaskEntry, type TaskStatusFilter } from './queries';

const PAGE_SIZE = 25;
const GRID_TEMPLATE = '0.5fr 1.2fr 1.6fr 0.7fr 0.7fr 0.9fr 1.1fr';

export default function TasksListPage() {
  const [status, setStatus] = useState<TaskStatusFilter>('unresolved');
  const [page, setPage] = useState(0);
  const navigate = useNavigate();

  const { data, isLoading, isError, error, refetch } = useTasksQuery({ status, page, pageSize: PAGE_SIZE });

  const entries = data?.entries ?? [];
  const totalCount = data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  const columns = useMemo<Column<TaskEntry>[]>(() => [
    { key: 'id', header: 'Id', render: t => <span className="text-text-muted mono">#{t.id}</span> },
    {
      key: 'created',
      header: 'Created',
      render: t => <span title={formatDateTime(t.createdAt)}>{formatRelativeTime(t.createdAt)}</span>,
    },
    {
      key: 'subscription',
      header: 'Subscription',
      render: t => (
        <div>
          <div className="font-semibold text-text">{t.subscriptionName}</div>
          <div className="text-text-muted text-xs">{t.queryName}</div>
        </div>
      ),
    },
    { key: 'latest', header: 'Latest', render: t => formatNumber(t.latestResultCount) },
    { key: 'execs', header: 'Execs', render: t => formatNumber(t.executionCount) },
    { key: 'unique', header: 'Unique', render: t => formatNumber(t.uniqueResultCounts) },
    {
      key: 'status',
      header: 'Status',
      render: t => t.resolved
        ? <Pill tone="ok">Resolved</Pill>
        : <Pill tone="warn">Unresolved</Pill>,
    },
  ], []);

  const onChangeStatus = (next: TaskStatusFilter) => {
    setStatus(next);
    setPage(0);
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Workload"
        emphasis="Tasks"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(totalCount)} {status} task{totalCount === 1 ? '' : 's'}</span>
        }
        actions={
          <Button icon={<RefreshCw />} onClick={() => refetch()} disabled={isLoading}>
            Refresh
          </Button>
        }
      />

      <Card className="p-3 flex gap-2 items-center">
        <span className="text-text-muted text-xs mr-1">Filter:</span>
        {(['unresolved', 'resolved', 'all'] as TaskStatusFilter[]).map(s => (
          <Button
            key={s}
            variant={status === s ? 'primary' : 'secondary'}
            size="sm"
            onClick={() => onChangeStatus(s)}
          >
            {s.charAt(0).toUpperCase() + s.slice(1)}
          </Button>
        ))}
      </Card>

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load tasks"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={entries}
          rowKey={t => t.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={t => navigate(`/tasks/${t.id}`)}
          empty={
            <EmptyState
              icon={<Check />}
              title={isLoading ? 'Loading tasks…' : 'No tasks here'}
              description={isLoading ? '' : `No ${status === 'all' ? '' : status} tasks to show.`}
            />
          }
        />
      )}

      {totalPages > 1 && (
        <Card className={cn('p-2.5 flex justify-between items-center')}>
          <span className="text-text-muted text-xs">
            Page {page + 1} of {totalPages}
          </span>
          <div className="flex gap-1.5">
            <Button size="sm" onClick={() => setPage(p => Math.max(0, p - 1))} disabled={page === 0 || isLoading}>
              Previous
            </Button>
            <Button size="sm" onClick={() => setPage(p => Math.min(totalPages - 1, p + 1))} disabled={page >= totalPages - 1 || isLoading}>
              Next
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
}
