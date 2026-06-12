import { useNavigate, useParams } from 'react-router-dom';
import { AlertTriangle, ChevronRight, Layers } from 'lucide-react';
import { PageHeader } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useQueryVersionsQuery, type QueryVersionSummary } from './queries';

const COLUMNS: Column<QueryVersionSummary>[] = [
  {
    key: 'version',
    header: 'Version',
    render: v => <span className="mono">v{v.versionNumber ?? '—'}</span>,
  },
  {
    key: 'name',
    header: 'Name',
    render: v => (
      <div>
        <div className="font-semibold text-text">{v.name ?? '—'}</div>
        {v.label && <div className="text-xs text-text-muted mt-0.5">{v.label}</div>}
      </div>
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
    render: v => <span className="text-text-muted">{v.changeSource ?? '—'}</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: v => (
      <span className="text-text-muted mono">{v.createdTime ? formatDateTime(v.createdTime) : '—'}</span>
    ),
  },
  {
    key: 'chevron',
    header: '',
    render: () => <ChevronRight size={14} className="text-text-muted" />,
  },
];

const GRID_TEMPLATE = '0.6fr 2fr 0.6fr 1fr 1.4fr 28px';

export default function QueryVersionsPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const queryId = id ? Number.parseInt(id, 10) : Number.NaN;
  const { data, isLoading, isError, error } = useQueryVersionsQuery(queryId);

  if (Number.isNaN(queryId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader eyebrow="Query" emphasis="versions" />
        <EmptyState icon={<AlertTriangle size={20} />} title="Invalid query id" description={String(id)} />
      </div>
    );
  }

  const versions = data ?? [];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Query"
        emphasis={`Query #${queryId} · versions`}
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(versions.length)} versions</span>
        }
      />

      {isError && (
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load versions"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={COLUMNS}
          rows={versions}
          rowKey={v => v.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={v => navigate(`/queries/${queryId}/versions/${v.id}`)}
          empty={
            <EmptyState
              icon={<Layers size={20} />}
              title={isLoading ? 'Loading versions…' : 'No versions yet'}
              description={isLoading ? '' : 'Versions appear when the query is edited.'}
            />
          }
        />
      )}
    </div>
  );
}
