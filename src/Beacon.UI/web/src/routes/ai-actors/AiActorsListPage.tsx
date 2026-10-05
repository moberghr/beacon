import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Bot, Plus, RefreshCw } from 'lucide-react';
import { Button, Card, CardBody, Field, PageHeader, Pill } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { DataSourcePicker } from '@/routes/data-sources/DataSourcePicker';
import { useAiActorsList, ACTOR_STATUS_LABEL, type AiActorListItem } from './queries';
import { CreateAiActorDialog } from './CreateAiActorDialog';

const GRID_TEMPLATE = '0.6fr 1.6fr 2fr 1fr 0.7fr 1fr 1fr';

export default function AiActorsListPage() {
  const navigate = useNavigate();
  const list = useAiActorsList();
  const dataSourceId = list.filters.dataSourceId === '' ? undefined : Number(list.filters.dataSourceId);
  const includeArchived = list.filters.includeArchived === 'true';
  const [createOpen, setCreateOpen] = useState(false);

  const { data, isLoading, isError, error, refetch } = list;
  const actors = list.items;

  const columns = useMemo<Column<AiActorListItem>[]>(() => [
    { key: 'id', header: 'Id', sortKey: 'actorId', render: r => <span className="text-text-muted mono">{r.actorId}</span> },
    {
      key: 'name',
      sortKey: 'name',
      header: 'Name',
      render: r => <span className="font-semibold text-text">{r.name ?? '—'}</span>,
    },
    {
      key: 'instructions',
      header: 'Instructions',
      render: r => (
        <span className="text-text-muted block overflow-hidden text-ellipsis whitespace-nowrap">
          {r.instructions ?? '—'}
        </span>
      ),
    },
    {
      key: 'source',
      sortKey: 'dataSourceName',
      header: 'Data source',
      render: r => <span>{r.dataSourceName ?? '—'}</span>,
    },
    {
      key: 'status',
      sortKey: 'status',
      header: 'Status',
      render: r => <Pill>{ACTOR_STATUS_LABEL[r.status ?? 0] ?? r.status}</Pill>,
    },
    {
      key: 'thinks',
      sortKey: 'thinkCount',
      header: 'Think cycles',
      render: r => formatNumber(r.thinkCount ?? 0),
    },
    {
      key: 'last',
      sortKey: 'lastThinkTime',
      header: 'Last think',
      render: r => r.lastThinkTime
        ? <span className="text-text-muted mono">{formatDateTime(r.lastThinkTime)}</span>
        : <span className="text-text-muted">—</span>,
    },
  ], []);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        eyebrow="Automation"
        prefix="AI"
        emphasis="actors"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(data?.totalCount ?? 0)} actor(s)</span>
        }
        actions={
          <>
            <Button variant="primary" onClick={() => setCreateOpen(true)} icon={<Plus />}>
              New actor
            </Button>
            <Button
              onClick={() => refetch()}
              disabled={isLoading}
              icon={<RefreshCw />}
            >
              Refresh
            </Button>
          </>
        }
      />

      <Card>
        <CardBody>
          <div className="flex gap-3 items-end flex-wrap">
            <Field label="Data source" className="min-w-[240px]">
              <DataSourcePicker
                value={dataSourceId}
                onSelect={x => list.setFilter('dataSourceId', x ? String(x.id) : '')}
                clearLabel="All data sources"
              />
            </Field>
            <label className="flex items-center gap-1.5 pb-1.5">
              <input
                type="checkbox"
                checked={includeArchived}
                onChange={e => list.setFilter('includeArchived', e.target.checked ? 'true' : '')}
              />
              <span className="text-sm">Include archived</span>
            </label>
          </div>
        </CardBody>
      </Card>

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load actors"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={actors}
          rowKey={r => r.actorId ?? 0}
          {...list.tableProps}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={r => r.actorId && navigate(`/ai-actors/${r.actorId}`)}
          empty={
            <EmptyState
              icon={<Bot />}
              title={isLoading ? 'Loading actors…' : dataSourceId === undefined ? 'No AI actors yet' : 'No actors for this data source'}
              description={isLoading ? '' : 'Use the "New actor" button to create one.'}
              action={!isLoading ? (
                <Button
                  type="button"
                  variant="primary"
                  onClick={() => setCreateOpen(true)}
                  icon={<Plus />}
                >
                  New actor
                </Button>
              ) : undefined}
            />
          }
        />
      )}

      <CreateAiActorDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        initialDataSourceId={dataSourceId}
      />
    </div>
  );
}
