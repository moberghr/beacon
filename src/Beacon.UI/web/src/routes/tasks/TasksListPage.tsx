import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Check, RefreshCw } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, Card, PageHeader, Pill } from '@/components/beacon';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { useTasksList, type TaskEntry, type TaskStatusFilter } from './queries';

const GRID_TEMPLATE = '0.5fr 1.2fr 1.6fr 0.7fr 0.7fr 0.9fr 1.1fr';

export default function TasksListPage() {
  const navigate = useNavigate();
  const list = useTasksList();
  const { items, isLoading, isError, error, refetch } = list;
  const status = list.filters.status;
  const totalCount = list.data?.totalCount ?? 0;

  const columns = useMemo<Column<TaskEntry>[]>(() => [
    { key: 'id', header: 'Id', sortKey: 'id', render: t => <span className="text-text-muted mono">#{t.id}</span> },
    {
      key: 'created',
      header: 'Created',
      sortKey: 'createdAt',
      render: t => <span title={formatDateTime(t.createdAt)}>{formatRelativeTime(t.createdAt)}</span>,
    },
    {
      key: 'subscription',
      header: 'Subscription',
      sortKey: 'subscriptionName',
      render: t => (
        <div>
          <div className="font-semibold text-text">{t.subscriptionName}</div>
          <div className="text-text-muted text-xs">{t.queryName}</div>
        </div>
      ),
    },
    { key: 'latest', header: 'Latest', sortKey: 'latestResultCount', align: 'right', render: t => formatNumber(t.latestResultCount) },
    { key: 'execs', header: 'Execs', sortKey: 'executionCount', align: 'right', render: t => formatNumber(t.executionCount) },
    { key: 'unique', header: 'Unique', sortKey: 'uniqueResultCounts', align: 'right', render: t => formatNumber(t.uniqueResultCounts) },
    {
      key: 'status',
      header: 'Status',
      sortKey: 'resolved',
      render: t => t.resolved
        ? <Pill tone="ok">Resolved</Pill>
        : <Pill tone="warn">Unresolved</Pill>,
    },
  ], []);

  const onChangeStatus = (next: TaskStatusFilter) => list.setFilter('status', next);

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
          rows={items}
          rowKey={t => t.id}
          {...list.tableProps}
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

    </div>
  );
}
