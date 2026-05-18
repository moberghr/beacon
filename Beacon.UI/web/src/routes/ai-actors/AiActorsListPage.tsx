import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Bot, Plus, RefreshCw } from 'lucide-react';
import { Button, Card, CardBody, Field, PageHeader, Pill, Select } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import type { AiActorListItem } from '@/api/generated/beacon-api';
import { useAiActorsQuery, ACTOR_STATUS_LABEL } from './queries';
import { CreateAiActorDialog } from './CreateAiActorDialog';

const GRID_TEMPLATE = '0.6fr 1.6fr 2fr 1fr 0.7fr 1fr 1fr';

export default function AiActorsListPage() {
  const navigate = useNavigate();
  const dsQuery = useDataSourcesQuery();
  const [dataSourceId, setDataSourceId] = useState<number | undefined>(undefined);
  const [includeArchived, setIncludeArchived] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);

  const { data, isLoading, isError, error, refetch } = useAiActorsQuery(dataSourceId, includeArchived);
  const actors = data?.actors ?? [];

  const columns = useMemo<Column<AiActorListItem>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="text-text-muted mono">{r.actorId}</span> },
    {
      key: 'name',
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
      header: 'Data source',
      render: r => <span>{r.dataSourceName ?? '—'}</span>,
    },
    {
      key: 'status',
      header: 'Status',
      render: r => <Pill>{ACTOR_STATUS_LABEL[r.status ?? 0] ?? r.status}</Pill>,
    },
    {
      key: 'thinks',
      header: 'Think cycles',
      render: r => formatNumber(r.thinkCount ?? 0),
    },
    {
      key: 'last',
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
          dataSourceId === undefined
            ? <span className="text-text-muted">Pick a data source to list its AI actors.</span>
            : isLoading
              ? <span className="text-text-muted">Loading…</span>
              : <span className="text-text-muted">{formatNumber(actors.length)} actor(s)</span>
        }
        actions={
          <>
            <Button variant="primary" onClick={() => setCreateOpen(true)} icon={<Plus />}>
              New actor
            </Button>
            <Button
              onClick={() => refetch()}
              disabled={isLoading || dataSourceId === undefined}
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
              <Select
                value={dataSourceId ?? ''}
                onChange={e => setDataSourceId(e.target.value ? Number(e.target.value) : undefined)}
              >
                <option value="">— Select —</option>
                {(dsQuery.data?.entries ?? []).map(d => (
                  <option key={d.id} value={d.id}>{d.name}</option>
                ))}
              </Select>
            </Field>
            <label className="flex items-center gap-1.5 pb-1.5">
              <input
                type="checkbox"
                checked={includeArchived}
                onChange={e => setIncludeArchived(e.target.checked)}
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

      {!isError && dataSourceId !== undefined && (
        <DataTable
          columns={columns}
          rows={actors}
          rowKey={r => r.actorId ?? 0}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={r => r.actorId && navigate(`/ai-actors/${r.actorId}`)}
          empty={
            <EmptyState
              icon={<Bot />}
              title={isLoading ? 'Loading actors…' : 'No actors for this data source'}
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
