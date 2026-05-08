import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import type { AiActorListItem } from '@/api/generated/beacon-api';
import { useAiActorsQuery, ACTOR_STATUS_LABEL } from './queries';

const GRID_TEMPLATE = '0.6fr 1.6fr 2fr 1fr 0.7fr 1fr 1fr';

export default function AiActorsListPage() {
  const navigate = useNavigate();
  const dsQuery = useDataSourcesQuery();
  const [dataSourceId, setDataSourceId] = useState<number | undefined>(undefined);
  const [includeArchived, setIncludeArchived] = useState(false);

  const { data, isLoading, isError, error, refetch } = useAiActorsQuery(dataSourceId, includeArchived);
  const actors = data?.actors ?? [];

  const columns = useMemo<Column<AiActorListItem>[]>(() => [
    { key: 'id', header: 'Id', render: r => <span className="muted mono">{r.actorId}</span> },
    {
      key: 'name',
      header: 'Name',
      render: r => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{r.name ?? '—'}</span>,
    },
    {
      key: 'instructions',
      header: 'Instructions',
      render: r => (
        <span className="muted" style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
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
      render: r => <span className="pill">{ACTOR_STATUS_LABEL[r.status ?? 0] ?? r.status}</span>,
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
        ? <span className="muted mono">{formatDateTime(r.lastThinkTime)}</span>
        : <span className="muted">—</span>,
    },
  ], []);

  return (
    <div className="page">
      <PageHeader
        title="AI actors"
        sub={
          dataSourceId === undefined
            ? <span className="muted">Pick a data source to list its AI actors.</span>
            : isLoading
              ? <span className="muted">Loading…</span>
              : <span className="muted">{formatNumber(actors.length)} actor(s)</span>
        }
        actions={
          <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading || dataSourceId === undefined}>
            <Icon.Refresh size={14} className="btn__icon" />
            Refresh
          </button>
        }
      />

      <div className="card" style={{ padding: 16, display: 'flex', gap: 12, alignItems: 'center', marginBottom: 16, flexWrap: 'wrap' }}>
        <label style={{ display: 'flex', flexDirection: 'column', minWidth: 240 }}>
          <span className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Data source</span>
          <select
            className="input"
            value={dataSourceId ?? ''}
            onChange={e => setDataSourceId(e.target.value ? Number(e.target.value) : undefined)}
          >
            <option value="">— Select —</option>
            {(dsQuery.data?.entries ?? []).map(d => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
        </label>
        <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <input
            type="checkbox"
            checked={includeArchived}
            onChange={e => setIncludeArchived(e.target.checked)}
          />
          <span>Include archived</span>
        </label>
      </div>

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load actors"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && dataSourceId !== undefined && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={actors}
            rowKey={r => r.actorId ?? 0}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={r => r.actorId && navigate(`/ai-actors/${r.actorId}`)}
            empty={
              <EmptyState
                icon={<Icon.Bot size={20} />}
                title={isLoading ? 'Loading actors…' : 'No actors for this data source'}
                description={isLoading ? '' : 'Create an actor in the legacy admin UI for now.'}
              />
            }
          />
        </div>
      )}
    </div>
  );
}
