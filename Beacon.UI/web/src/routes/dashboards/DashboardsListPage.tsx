import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, LayoutGrid, Plus, RefreshCw, X } from 'lucide-react';
import { PageHeader, Button, Card, Input, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { describeError } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import type { DashboardListData } from '@/api/generated/beacon-api';
import { useDashboardsQuery, useDeleteDashboard, useCreateDashboard } from './queries';

const GRID_TEMPLATE = '0.6fr 2fr 2.5fr 0.7fr 0.9fr 1.2fr 60px';

export default function DashboardsListPage() {
  const [search, setSearch] = useState('');
  const { data, isLoading, isError, error, refetch } = useDashboardsQuery(search.trim() || undefined);
  const deleteMutation = useDeleteDashboard();
  const createMutation = useCreateDashboard();
  const navigate = useNavigate();

  const [deleting, setDeleting] = useState<DashboardListData | null>(null);
  const [creatingName, setCreatingName] = useState('');

  const rows = data?.data ?? [];
  const totalCount = data?.totalCount ?? 0;

  const columns = useMemo<Column<DashboardListData>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="mono text-text-muted">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span className="font-semibold text-text">{r.name}</span>,
    },
    {
      key: 'description',
      header: 'Description',
      render: r => r.description
        ? <span className="text-text-muted">{r.description}</span>
        : <span className="text-text-muted">—</span>,
    },
    {
      key: 'shared',
      header: 'Shared',
      render: r => r.isShared ? <Pill tone="info">Shared</Pill> : <span className="text-text-muted">—</span>,
    },
    {
      key: 'widgets',
      header: 'Widgets',
      render: r => formatNumber(r.widgetCount ?? 0),
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
          title="Delete dashboard"
          icon={<X />}
        />
      ),
    },
  ], []);

  async function handleCreateQuick() {
    const name = creatingName.trim();
    if (!name) {
      toast.error('Dashboard name is required');
      return;
    }
    try {
      const result = await createMutation.mutateAsync({ name, isShared: false });
      toast.success('Dashboard created');
      setCreatingName('');
      if (result?.id) {
        navigate(`/dashboards/${result.id}/edit`);
      }
    } catch (e) {
      toast.error(describeError(e, 'Failed to create dashboard'));
    }
  }

  async function handleDelete() {
    if (!deleting?.id) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success('Dashboard deleted');
      setDeleting(null);
    } catch (e) {
      toast.error(describeError(e, 'Failed to delete dashboard'));
    }
  }

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Dashboards"
        prefix="Composing"
        emphasis="dashboards"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(rows.length)} of {formatNumber(totalCount)}</span>
        }
        actions={
          <div className="flex gap-2 items-center">
            <Input
              placeholder="Search…"
              value={search}
              onChange={e => setSearch(e.target.value)}
              className="w-[200px]"
            />
            <Button type="button" onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
          </div>
        }
      />

      <Card className="p-4">
        <div className="flex gap-2 items-center">
          <Input
            placeholder="New dashboard name…"
            value={creatingName}
            onChange={e => setCreatingName(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') handleCreateQuick(); }}
            className="flex-1"
          />
          <Button
            variant="primary"
            type="button"
            onClick={handleCreateQuick}
            disabled={createMutation.isPending || !creatingName.trim()}
            icon={<Plus />}
          >
            New dashboard
          </Button>
        </div>
      </Card>

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load dashboards"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={r => r.id ?? 0}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={r => r.id && navigate(`/dashboards/${r.id}`)}
          empty={
            <EmptyState
              icon={<LayoutGrid />}
              title={isLoading ? 'Loading dashboards…' : 'No dashboards yet'}
              description={isLoading ? '' : 'Create your first dashboard above.'}
            />
          }
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        title="Delete dashboard"
        message={deleting ? <>Delete <strong>{deleting.name}</strong>? This cannot be undone.</> : null}
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onCancel={() => setDeleting(null)}
        onConfirm={handleDelete}
      />
    </div>
  );
}
