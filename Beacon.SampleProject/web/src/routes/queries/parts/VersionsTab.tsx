import { Link } from 'react-router-dom';
import type { QueryVersionSummary } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useQueryVersionsQuery } from '../queries';

interface VersionsTabProps {
  queryId: number;
}

const GRID_TEMPLATE = '0.6fr 2fr 0.6fr 1fr 1.4fr';

export function VersionsTab({ queryId }: VersionsTabProps) {
  const { data, isLoading, isError, error } = useQueryVersionsQuery(queryId);

  if (isError) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load versions"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const versions = data ?? [];

  const columns: Column<QueryVersionSummary>[] = [
    {
      key: 'version',
      header: 'Version',
      render: v => <span className="mono">v{v.versionNumber ?? '—'}</span>,
    },
    {
      key: 'name',
      header: 'Name',
      render: v => (
        <Link
          to={`/queries/${queryId}/versions/${v.id}`}
          style={{ color: 'var(--brand-600)', fontWeight: 500 }}
        >
          {v.name ?? '—'}
        </Link>
      ),
    },
    {
      key: 'steps',
      header: 'Steps',
      render: v => formatNumber(v.stepCount ?? 0),
    },
    {
      key: 'source',
      header: 'Source',
      render: v => <span className="muted">{v.changeSource ?? '—'}</span>,
    },
    {
      key: 'created',
      header: 'Created',
      render: v => (
        <span className="muted mono">{v.createdTime ? formatDateTime(v.createdTime) : '—'}</span>
      ),
    },
  ];

  return (
    <DataTable
      columns={columns}
      rows={versions}
      rowKey={v => v.id ?? Math.random()}
      gridTemplate={GRID_TEMPLATE}
      empty={
        <EmptyState
          icon={<Icon.Clock size={20} />}
          title={isLoading ? 'Loading versions…' : 'No versions yet'}
          description={isLoading ? '' : 'Versions are saved when the query SQL is modified.'}
        />
      }
    />
  );
}
