import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useQueriesListQuery, type QueryListItem } from './queries';

const COLUMNS: Column<QueryListItem>[] = [
  {
    key: 'name',
    header: 'Name',
    render: q => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{q.name}</div>
        {q.description && (
          <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{q.description}</div>
        )}
      </div>
    ),
  },
  {
    key: 'source',
    header: 'Source',
    render: q => q.aiActorName ? (
      <span className="pill pill--info mono" style={{ fontSize: 10 }}>{q.aiActorName}</span>
    ) : (
      <span className="muted">user-defined</span>
    ),
  },
  {
    key: 'steps',
    header: 'Steps',
    render: q => formatNumber(q.steps.length),
  },
  {
    key: 'subs',
    header: 'Subscriptions',
    render: q => formatNumber(q.subscriptionsCount),
  },
  {
    key: 'created',
    header: 'Created',
    render: q => <span className="mono">{formatDateTime(q.createdTime)}</span>,
  },
];

export default function QueriesListPage() {
  const [search, setSearch] = useState('');
  const navigate = useNavigate();
  const { data, isLoading } = useQueriesListQuery({ searchTerm: search.trim() || undefined });
  const entries = data?.items ?? [];

  return (
    <div className="page">
      <PageHeader
        title="Queries"
        sub={
          <span className="muted">
            {data ? `${formatNumber(data.totalCount)} total` : isLoading ? 'Loading…' : ''}
          </span>
        }
        actions={
          <Link to="/queries/new" className="btn btn--primary">
            <Icon.Plus size={14} className="btn__icon" /> New query
          </Link>
        }
      />

      <div style={{ marginBottom: 12, display: 'flex', gap: 8 }}>
        <input
          type="search"
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Search queries by name…"
          className="q-input"
          style={{ maxWidth: 360 }}
        />
      </div>

      <div className="card">
        <DataTable
          columns={COLUMNS}
          rows={entries}
          rowKey={q => q.queryId}
          gridTemplate="2fr 1fr 0.6fr 0.8fr 1.2fr"
          onRowClick={q => navigate(`/queries/${q.queryId}`)}
          empty={
            <EmptyState
              icon={<Icon.Layers size={20} />}
              title={isLoading ? 'Loading queries…' : 'No queries yet'}
              description="Click + New query to start; you'll fill in the SQL on the editor page."
            />
          }
        />
      </div>
    </div>
  );
}
