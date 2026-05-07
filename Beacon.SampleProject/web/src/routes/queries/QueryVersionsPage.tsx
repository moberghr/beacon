import { useNavigate, useParams } from 'react-router-dom';
import type { QueryVersionSummary } from '@/api/generated/beacon-api';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useQueryVersionsQuery } from './queries';

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
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{v.name ?? '—'}</div>
        {v.label && <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{v.label}</div>}
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
    render: v => <span className="muted">{v.changeSource ?? '—'}</span>,
  },
  {
    key: 'created',
    header: 'Created',
    render: v => (
      <span className="muted mono">{v.createdTime ? formatDateTime(v.createdTime) : '—'}</span>
    ),
  },
  {
    key: 'chevron',
    header: '',
    render: () => <Icon.Chevron size={14} className="muted" />,
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
      <div className="page">
        <PageHeader title="Query versions" />
        <EmptyState icon={<Icon.Alert size={20} />} title="Invalid query id" description={String(id)} />
      </div>
    );
  }

  const versions = data ?? [];

  return (
    <div className="page">
      <PageHeader
        title={`Query #${queryId} · versions`}
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(versions.length)} versions</span>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load versions"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={COLUMNS}
            rows={versions}
            rowKey={v => v.id ?? Math.random()}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={v => v.id && navigate(`/app/queries/${queryId}/versions/${v.id}`)}
            empty={
              <EmptyState
                icon={<Icon.Layers size={20} />}
                title={isLoading ? 'Loading versions…' : 'No versions yet'}
                description={isLoading ? '' : 'Versions appear when the query is edited.'}
              />
            }
          />
        </div>
      )}
    </div>
  );
}
