import { Link, useNavigate } from 'react-router-dom';
import { AlertTriangle, Layers, Plus } from 'lucide-react';
import { Button, Input, PageHeader, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useSearchFilter } from '@/lib/usePagedList';
import { useQueriesList, type QueryListItem } from './queries';

const COLUMNS: Column<QueryListItem>[] = [
  {
    key: 'name',
    header: 'Name',
    sortKey: 'name',
    render: q => (
      <div>
        <div className="font-semibold text-text">{q.name}</div>
        {q.description && (
          <div className="text-xs text-text-muted mt-0.5">{q.description}</div>
        )}
      </div>
    ),
  },
  {
    key: 'source',
    header: 'Source',
    render: q => q.aiActorName ? (
      <Pill tone="info" className="mono normal-case tracking-normal">{q.aiActorName}</Pill>
    ) : (
      <span className="text-text-muted">user-defined</span>
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
    sortKey: 'subscriptionsCount',
    align: 'right',
    render: q => formatNumber(q.subscriptionsCount),
  },
  {
    key: 'created',
    header: 'Created',
    sortKey: 'createdTime',
    render: q => <span className="mono">{formatDateTime(q.createdTime)}</span>,
  },
];

export default function QueriesListPage() {
  const navigate = useNavigate();
  const list = useQueriesList();
  const { data, items, isLoading, isError, error, refetch } = list;
  const [search, setSearch] = useSearchFilter(list.filters.searchTerm, value => list.setFilter('searchTerm', value));

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Saved"
        prefix="Your"
        emphasis="queries"
        sub={
          <span className="text-text-muted">
            {data ? `${formatNumber(data.totalCount)} total` : isLoading ? 'Loading…' : ''}
          </span>
        }
        actions={
          <Link to="/queries/new">
            <Button variant="primary" icon={<Plus />}>New query</Button>
          </Link>
        }
      />

      <div className="flex gap-2">
        <Input
          type="search"
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Search queries by name…"
          className="max-w-[360px]"
        />
      </div>

      {isError ? (
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load queries"
          description={error instanceof Error ? error.message : 'Unknown error'}
          action={
            <Button variant="primary" onClick={() => refetch()}>
              Retry
            </Button>
          }
        />
      ) : (
        <DataTable
          columns={COLUMNS}
          rows={items}
          rowKey={q => q.queryId}
          {...list.tableProps}
          gridTemplate="2fr 1fr 0.6fr 0.8fr 1.2fr"
          onRowClick={q => navigate(`/queries/${q.queryId}`)}
          ariaLabel="Saved queries"
          empty={
            <EmptyState
              icon={<Layers size={20} />}
              title={isLoading ? 'Loading queries…' : 'No queries yet'}
              description="Click + New query to start; you'll fill in the SQL on the editor page."
            />
          }
        />
      )}

    </div>
  );
}
