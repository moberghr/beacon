import { useNavigate } from 'react-router-dom';
import type { ProjectSummaryEntry } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatRelativeTime, formatNumber } from '@/lib/format';
import { useProjectsQuery } from './queries';

const COLUMNS: Column<ProjectSummaryEntry>[] = [
  {
    key: 'name',
    header: 'Name',
    render: p => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{p.name}</div>
        {p.description && (
          <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 2 }}>
            {p.description.length > 80 ? `${p.description.slice(0, 80)}…` : p.description}
          </div>
        )}
      </div>
    ),
  },
  {
    key: 'datasources',
    header: 'Sources',
    render: p => formatNumber(p.dataSourceCount),
  },
  {
    key: 'repos',
    header: 'Repos',
    render: p => formatNumber(p.repositoryCount),
  },
  {
    key: 'lastScan',
    header: 'Last scan',
    render: p =>
      p.lastScanAt
        ? <span title={String(p.lastScanAt)}>{formatRelativeTime(p.lastScanAt)}</span>
        : <span className="muted">—</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: p => <span className="muted">{formatRelativeTime(p.createdAt)}</span>,
  },
  {
    key: 'chevron',
    header: '',
    render: () => <Icon.Chevron size={14} className="muted" />,
  },
];

const GRID_TEMPLATE = '2.4fr 0.7fr 0.7fr 1.1fr 1.1fr 28px';

export default function ProjectsListPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useProjectsQuery();

  const entries = data?.entries ?? [];

  return (
    <div className="page">
      <PageHeader
        title="Projects"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
            <Icon.Refresh size={14} className="btn__icon" />
            Refresh
          </button>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load projects"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <button className="btn btn--primary" type="button" onClick={() => refetch()}>
              Retry
            </button>
          }
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={COLUMNS}
            rows={entries}
            rowKey={p => p.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={p => navigate(`/projects/${p.id}`)}
            empty={
              <EmptyState
                icon={<Icon.Folder size={20} />}
                title={isLoading ? 'Loading projects…' : 'No projects yet'}
                description={isLoading ? '' : 'Projects will appear here once created.'}
                className=""
              />
            }
          />
        </div>
      )}
    </div>
  );
}
