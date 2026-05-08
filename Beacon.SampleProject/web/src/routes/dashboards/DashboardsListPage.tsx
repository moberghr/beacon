import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ApiError } from '@/lib/api';
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
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.id}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name}</span>,
    },
    {
      key: 'description',
      header: 'Description',
      render: r => r.description
        ? <span className="muted">{r.description}</span>
        : <span className="muted">—</span>,
    },
    {
      key: 'shared',
      header: 'Shared',
      render: r => r.isShared ? <span className="pill pill--info">Shared</span> : <span className="muted">—</span>,
    },
    {
      key: 'widgets',
      header: 'Widgets',
      render: r => formatNumber(r.widgetCount ?? 0),
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
          title="Delete dashboard"
        >
          <Icon.X size={14} />
        </button>
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
      toast.error(e instanceof ApiError ? e.body || e.message : 'Failed to create dashboard');
    }
  }

  async function handleDelete() {
    if (!deleting?.id) return;
    try {
      await deleteMutation.mutateAsync(deleting.id);
      toast.success('Dashboard deleted');
      setDeleting(null);
    } catch (e) {
      toast.error(e instanceof ApiError ? e.body || e.message : 'Failed to delete dashboard');
    }
  }

  return (
    <div className="page">
      <PageHeader
        title="Dashboards"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(rows.length)} of {formatNumber(totalCount)}</span>
        }
        actions={
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <input
              className="input"
              placeholder="Search…"
              value={search}
              onChange={e => setSearch(e.target.value)}
              style={{ width: 200 }}
            />
            <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
          </div>
        }
      />

      <div className="card" style={{ padding: 16, display: 'flex', gap: 8, alignItems: 'center', marginBottom: 16 }}>
        <input
          className="input"
          placeholder="New dashboard name…"
          value={creatingName}
          onChange={e => setCreatingName(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') handleCreateQuick(); }}
          style={{ flex: 1 }}
        />
        <button
          className="btn btn--primary"
          type="button"
          onClick={handleCreateQuick}
          disabled={createMutation.isPending || !creatingName.trim()}
        >
          + New dashboard
        </button>
      </div>

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load dashboards"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={r => r.id ?? 0}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={r => r.id && navigate(`/dashboards/${r.id}`)}
            empty={
              <EmptyState
                icon={<Icon.Grid size={20} />}
                title={isLoading ? 'Loading dashboards…' : 'No dashboards yet'}
                description={isLoading ? '' : 'Create your first dashboard above.'}
              />
            }
          />
        </div>
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
