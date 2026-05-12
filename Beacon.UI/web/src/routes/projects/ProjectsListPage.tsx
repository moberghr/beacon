import { Link, useNavigate } from 'react-router-dom';
import { AlertTriangle, ChevronRight, Folder, Plus, RefreshCw } from 'lucide-react';
import type { ProjectSummaryEntry } from '@/api/generated/beacon-api';
import { Button, PageHeader } from '@/components/beacon';
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
        <div className="font-semibold text-text">{p.name}</div>
        {p.description && (
          <div className="text-xs text-text-muted mt-0.5">
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
        : <span className="text-text-muted">—</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: p => <span className="text-text-muted">{formatRelativeTime(p.createdAt)}</span>,
  },
  {
    key: 'chevron',
    header: '',
    render: () => <ChevronRight size={14} className="text-text-muted" />,
  },
];

const GRID_TEMPLATE = '2.4fr 0.7fr 0.7fr 1.1fr 1.1fr 28px';

export default function ProjectsListPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = useProjectsQuery();

  const entries = data?.entries ?? [];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Workspaces"
        emphasis="Projects"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} total</span>
        }
        actions={
          <>
            <Button onClick={() => refetch()} disabled={isLoading} icon={<RefreshCw />}>
              Refresh
            </Button>
            <Link to="/projects/new">
              <Button variant="primary" icon={<Plus />} type="button">
                New project
              </Button>
            </Link>
          </>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load projects"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      )}

      {!isError && (
        <DataTable
          columns={COLUMNS}
          rows={entries}
          rowKey={p => p.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={p => navigate(`/projects/${p.id}`)}
          empty={
            <EmptyState
              icon={<Folder />}
              title={isLoading ? 'Loading projects…' : 'No projects yet'}
              description={isLoading ? '' : 'Projects will appear here once created.'}
            />
          }
        />
      )}
    </div>
  );
}
