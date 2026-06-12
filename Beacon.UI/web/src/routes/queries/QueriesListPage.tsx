import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AlertTriangle, ChevronLeft, ChevronRight, Layers, Plus } from 'lucide-react';
import { Button, Input, PageHeader, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useQueriesListQuery, type QueryListItem } from './queries';

/** Debounce a fast-changing value (search input) before it enters a query key. */
function useDebouncedValue<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

const PAGE_SIZE = 50;

const COLUMNS: Column<QueryListItem>[] = [
  {
    key: 'name',
    header: 'Name',
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
  const [page, setPage] = useState(1);
  const navigate = useNavigate();
  const debouncedSearch = useDebouncedValue(search.trim());
  const { data, isLoading, isError, error, refetch } = useQueriesListQuery({
    searchTerm: debouncedSearch || undefined,
    page,
    pageSize: PAGE_SIZE,
  });
  const entries = data?.items ?? [];
  const totalPages = Math.max(1, Math.ceil((data?.totalCount ?? 0) / PAGE_SIZE));

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
          onChange={e => { setSearch(e.target.value); setPage(1); }}
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
          rows={entries}
          rowKey={q => q.queryId}
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

      {!isError && totalPages > 1 && (
        <div className="flex items-center justify-end gap-2 text-xs text-text-muted">
          <Button
            size="sm"
            icon={<ChevronLeft />}
            onClick={() => setPage(p => Math.max(1, p - 1))}
            disabled={page <= 1}
          >
            Prev
          </Button>
          <span className="tabular-nums">Page {page} of {totalPages}</span>
          <Button
            size="sm"
            icon={<ChevronRight />}
            onClick={() => setPage(p => Math.min(totalPages, p + 1))}
            disabled={page >= totalPages}
          >
            Next
          </Button>
        </div>
      )}
    </div>
  );
}
