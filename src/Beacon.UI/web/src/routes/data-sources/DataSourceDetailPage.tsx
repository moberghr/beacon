import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, Ban, Database, GitFork, Hourglass, Key, KeyRound, Layers, RefreshCw, X } from 'lucide-react';
import {
  PageHeader,
  Banner,
  Button,
  Card,
  CardBody,
  KPI,
  KPIGrid,
  Pill,
} from '@/components/beacon';
import { Tabs, type TabDef } from '@/components/Tabs';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { describeError, httpErrorInfo, isNotFound, NOT_FOUND_DETAIL } from '@/lib/api';
import { useAccess } from '@/auth/useAuth';
import { ProjectChips } from './ProjectChips';
import { ENGINE_READ_ONLY_HINT, READ_ONLY_LOGIN_HINT, ReadOnlyEnforcementBadge, WRITE_TARGET_HINT } from './ReadOnlyEnforcementBadge';
import { MigrationWritesControl } from './MigrationWritesControl';
import { DataSourceEnabledControl } from './DataSourceEnabledControl';
import { DataSourceStateBadges, DISABLED_HINT } from './DataSourceStateBadges';
import { ReplaceConnectionDialog } from './ReplaceConnectionDialog';
import { AwaitingApprovalBanner, DATA_SOURCE_HELD_HINT, DATA_SOURCE_HELD_NO_REQUEST_HINT, DATA_SOURCE_HELD_NO_REQUEST_TITLE, DATA_SOURCE_HELD_TITLE } from '@/routes/approvals/AwaitingApproval';
import { BackgroundTaskState, ChangeApprovalKind } from '@/lib/enums';
import { isBackgroundTaskFinished } from '@/lib/useBackgroundTask';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import { formatNumber } from '@/lib/format';
import {
  useDataSourceQuery,
  useDataSourceMetadataQuery,
  useDataSourceMetadataRefreshStatus,
  useDeleteDataSource,
  useStartDataSourceMetadataRefresh,
  type ColumnMetadataDto,
  type DataSourceEntry,
  type TableMetadataDto,
} from './queries';

type TabKey = 'overview' | 'schema' | 'connection';

export default function DataSourceDetailPage() {
  const { id } = useParams();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;
  const navigate = useNavigate();

  const listQuery = useDataSourceQuery(Number.isFinite(numericId) ? numericId : null);
  const metadataQuery = useDataSourceMetadataQuery(Number.isFinite(numericId) ? numericId : null);
  const deleteMutation = useDeleteDataSource();
  const startRefresh = useStartDataSourceMetadataRefresh();
  // The refresh runs as a background task: the ticket is followed until it finishes, then the catalog is read again.
  const [refreshTicket, setRefreshTicket] = useState<BackgroundTaskTicket | null>(null);
  const refreshTask = useDataSourceMetadataRefreshStatus(numericId, refreshTicket);
  const queryClient = useQueryClient();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [tab, setTab] = useState<TabKey>('overview');
  const [replacing, setReplacing] = useState(false);
  // Deleting a data source is a DataSourceAdmin's (an Admin holds it).
  const canDelete = useAccess().hasPermission('DataSourceAdmin');

  const entry = listQuery.data ?? null;
  const finishedStatus = isBackgroundTaskFinished(refreshTask.status?.state) ? refreshTask.status : null;
  const refreshReadFailed = refreshTicket != null && refreshTask.error != null;

  useEffect(() => {
    if (refreshTicket == null || (finishedStatus == null && !refreshReadFailed)) {
      return;
    }
    if (finishedStatus?.state === BackgroundTaskState.Succeeded) {
      void queryClient.invalidateQueries({ queryKey: ['data-sources', numericId, 'metadata'] });
      const skipped = finishedStatus.warningCount ?? 0;
      if (skipped > 0) {
        // Names with quote, bracket, semicolon or control characters are not stored; the server logs them.
        toast.warning(
          `Metadata refreshed from source; ${skipped} table or column ${skipped === 1 ? 'name was' : 'names were'} left out because ${skipped === 1 ? 'it carries' : 'they carry'} quote, bracket, semicolon or control characters (listed in the server log).`,
        );
      } else {
        toast.success('Metadata refreshed from source');
      }
    } else if (finishedStatus != null) {
      toast.error(describeRefreshFailure(finishedStatus));
    } else {
      toast.error(describeError(refreshTask.error, 'Could not read the refresh progress'));
    }
    setRefreshTicket(null);
  }, [refreshTicket, finishedStatus, refreshReadFailed, refreshTask.error, numericId, queryClient]);

  const refreshing = refreshTicket != null || startRefresh.isPending;
  const refreshLabel = !refreshing
    ? 'Refresh metadata'
    : refreshTask.state === BackgroundTaskState.Running
      ? 'Refreshing metadata…'
      : 'Refresh queued…';

  if (Number.isNaN(numericId)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader variant="nodes" emphasis="Data source" />
        <EmptyState icon={<AlertTriangle />} title="Invalid data source id" description={String(id)} />
      </div>
    );
  }

  if (listQuery.isLoading) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="nodes"
          emphasis="Data source"
          sub={<span className="text-text-muted">Loading…</span>}
        />
      </div>
    );
  }

  if (listQuery.isError && !isNotFound(listQuery.error)) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="nodes"
          emphasis={`Data source #${numericId}`}
          sub={<Link to="/data-sources" className="text-text-muted">← back to data sources</Link>}
        />
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load data source"
          description={listQuery.error instanceof Error ? listQuery.error.message : 'Unknown error'}
        />
      </div>
    );
  }

  // A data source the caller may not read answers 404, like a missing one: both show the not-found state.
  if (!entry) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="nodes"
          emphasis={`Data source #${numericId}`}
          sub={<Link to="/data-sources" className="text-text-muted">← back to data sources</Link>}
        />
        <EmptyState
          icon={<Database />}
          title="Data source not found"
          description={NOT_FOUND_DETAIL}
        />
      </div>
    );
  }

  const handleDelete = async () => {
    try {
      await deleteMutation.mutateAsync(entry.id);
      toast.success(`Deleted data source '${entry.name}'`);
      navigate('/data-sources');
    } catch {
      // Error toast already raised by the delete mutation hook.
    }
  };

  // Replacing the connection and disabling are a DataSourceAdmin's too, never on a source the host application manages
  // (its connection is declared in code); `editSettings` comes only with the by-id read.
  const canEdit = canDelete && entry.editSettings != null && entry.editSettings.hostManaged !== true;

  const tableCount = metadataQuery.data?.tables.length ?? 0;
  const columnCount = metadataQuery.data?.tables.reduce((acc, t) => acc + t.columns.length, 0) ?? 0;

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Layers className="size-3.5" /> Overview</> },
    { key: 'schema', label: <><Database className="size-3.5" /> Schema</>, count: tableCount },
    { key: 'connection', label: <><Key className="size-3.5" /> Connection</> },
  ];

  return (
    <div className={`flex flex-col gap-5 p-7 ${tab === 'schema' ? 'h-dvh' : ''}`}>
      <PageHeader
        variant="nodes"
        eyebrow={<>Data <span className="eyebrow-sep">/</span> <span className="mono">#{entry.id}</span></>}
        prefix="Connecting"
        emphasis={entry.name}
        sub={
          <>
            <Link to="/data-sources" className="text-text-muted">Data sources</Link>
            <span className="mx-1.5">/</span>
            <span className="mono">#{entry.id}</span>
          </>
        }
        actions={
          <>
            <Button
              type="button"
              onClick={() => navigate(`/data-sources/${entry.id}/relationships`)}
              icon={<GitFork />}
            >
              Relationships
            </Button>
            {/* A migration write target is never read, so it has no catalog to refresh. */}
            {!entry.allowMigrationWrites && <Button
              type="button"
              onClick={async () => {
                try {
                  setRefreshTicket(await startRefresh.mutateAsync(entry.id));
                } catch (err) {
                  // 429: refreshed within the cooldown; the server says how long it is.
                  if (httpErrorInfo(err)?.status === 429) {
                    toast.warning(describeError(err, 'Metadata was refreshed recently; try again later.'));
                  } else {
                    toast.error(describeError(err, 'Refresh failed'));
                  }
                }
              }}
              disabled={refreshing || metadataQuery.isFetching}
              aria-busy={refreshing}
              icon={<RefreshCw className={refreshing ? 'animate-spin' : undefined} />}
            >
              {refreshLabel}
            </Button>}
            {canEdit && (
              <Button type="button" onClick={() => setReplacing(true)} icon={<KeyRound />}>
                Replace connection
              </Button>
            )}
            {canDelete && (
              <Button
                variant="danger"
                type="button"
                onClick={() => setConfirmDelete(true)}
                icon={<X />}
              >
                Delete
              </Button>
            )}
          </>
        }
      />

      <KPIGrid>
        <KPI dot="brand" label="Type" value={entry.dataSourceType} sub={entry.databaseEngineType ?? '—'} />
        <KPI dot="info" label="Tables" value={formatNumber(tableCount)} sub="from metadata" />
        <KPI dot="ok" label="Columns" value={formatNumber(columnCount)} sub="across all tables" />
        <KPI dot="warn" label="Queries" value={formatNumber(entry.queryCount)} sub={`${formatNumber(entry.migrationJobsCount)} migrations`} />
      </KPIGrid>

      <Card className={tab === 'schema' ? 'flex flex-col flex-1 min-h-0' : undefined}>
        <Tabs tabs={tabs} active={tab} onChange={setTab} />

        {tab === 'overview' && <OverviewTab entry={entry} canEdit={canEdit} metadataLoading={metadataQuery.isLoading} />}
        {tab === 'schema' && <SchemaTab query={metadataQuery} />}
        {tab === 'connection' && <ConnectionTab entry={entry} />}
      </Card>

      {canEdit && <ReplaceConnectionDialog entry={entry} open={replacing} onClose={() => setReplacing(false)} />}

      <ConfirmDialog
        open={confirmDelete}
        title="Delete data source"
        message={
          <>Delete data source <strong>{entry.name}</strong>? The data source is archived; deletion is refused while a live query or migration job still uses it.</>
        }
        confirmLabel="Delete"
        destructive
        busy={deleteMutation.isPending}
        onConfirm={handleDelete}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}

interface OverviewProps {
  entry: DataSourceEntry;
  canEdit: boolean;
  metadataLoading: boolean;
}

function OverviewTab({ entry, canEdit, metadataLoading }: OverviewProps) {
  const isWriteTarget = entry.allowMigrationWrites === true;
  return (
    <CardBody>
      {entry.approval != null && (
        <div className="mb-4">
          <AwaitingApprovalBanner
            kind={ChangeApprovalKind.DataSourceConnection}
            title={DATA_SOURCE_HELD_TITLE}
            sub={DATA_SOURCE_HELD_HINT}
            requestId={entry.approval.requestId}
          />
        </div>
      )}
      {entry.approval == null && entry.awaitsFirstApproval === true && (
        <Banner
          tone="warn"
          icon={<Hourglass />}
          title={DATA_SOURCE_HELD_NO_REQUEST_TITLE}
          sub={DATA_SOURCE_HELD_NO_REQUEST_HINT}
          className="mb-4"
        />
      )}
      {entry.disabled === true && (
        <Banner
          tone="crit"
          icon={<Ban />}
          title="Disabled"
          sub={DISABLED_HINT}
          className="mb-4"
        />
      )}
      {isWriteTarget && (
        <Banner
          tone="info"
          icon={<Database />}
          title="Migration write target"
          sub={WRITE_TARGET_HINT}
          className="mb-4"
        />
      )}
      {!isWriteTarget && entry.databaseReadOnlyEnforced === false && (
        <Banner
          tone="warn"
          icon={<AlertTriangle />}
          title="No database-level read-only enforcement"
          sub={READ_ONLY_LOGIN_HINT}
          className="mb-4"
        />
      )}
      <dl className="grid grid-cols-[180px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
        <dt className="text-text-muted">Identifier</dt>
        <dd className="mono">#{entry.id}</dd>
        <dt className="text-text-muted">Name</dt>
        <dd className="font-semibold">{entry.name}</dd>
        <dt className="text-text-muted">Source type</dt>
        <dd><Pill>{entry.dataSourceType}</Pill></dd>
        <dt className="text-text-muted">Engine</dt>
        <dd>
          {entry.databaseEngineType
            ? <span className="mono">{entry.databaseEngineType}</span>
            : <span className="text-text-muted">—</span>}
        </dd>
        <dt className="text-text-muted">Status</dt>
        <dd><DataSourceEnabledControl entry={entry} canManage={canEdit} /></dd>
        <dt className="text-text-muted">Read-only</dt>
        <dd className="flex flex-wrap items-center gap-2">
          {isWriteTarget && <span className="text-text-muted">Not read by Beacon</span>}
          {!isWriteTarget && entry.databaseReadOnlyEnforced === true && (
            <Pill tone="ok" title={ENGINE_READ_ONLY_HINT}>Enforced by the engine — still use a read-only login</Pill>
          )}
          {!isWriteTarget && entry.databaseReadOnlyEnforced === false && <ReadOnlyEnforcementBadge enforced={false} />}
          {!isWriteTarget && entry.databaseReadOnlyEnforced == null && <span className="text-text-muted">—</span>}
          <DataSourceStateBadges transportVerified={entry.transportVerified} approval={entry.approval} awaitsFirstApproval={entry.awaitsFirstApproval} />
        </dd>
        <dt className="text-text-muted">Migration writes</dt>
        <dd><MigrationWritesControl entry={entry} /></dd>
        <dt className="text-text-muted">Metadata loading</dt>
        <dd>{entry.metadataLoadingEnabled
          ? <Pill tone="ok">Enabled</Pill>
          : <span className="text-text-muted">Disabled</span>}</dd>
        <dt className="text-text-muted">Projects</dt>
        <dd>
          <ProjectChips
            projects={entry.projects ?? []}
            otherProjectCount={entry.otherProjectCount ?? 0}
            openWhenUnlinked={entry.openWhenUnlinked ?? true}
            linked
          />
        </dd>
        <dt className="text-text-muted">Queries</dt>
        <dd>{formatNumber(entry.queryCount)}</dd>
        <dt className="text-text-muted">Migration jobs</dt>
        <dd>{formatNumber(entry.migrationJobsCount)}</dd>
      </dl>

      {metadataLoading && (
        <div className="text-xs text-text-muted mt-4">Loading metadata…</div>
      )}
    </CardBody>
  );
}

function SchemaTab({ query }: { query: ReturnType<typeof useDataSourceMetadataQuery> }) {
  const [selected, setSelected] = useState<{ schemaName: string; tableName: string } | null>(null);

  if (query.isLoading) {
    return <CardBody><span className="text-text-muted">Loading schema…</span></CardBody>;
  }

  if (query.isError) {
    return (
      <CardBody>
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load metadata"
          description={query.error instanceof Error ? query.error.message : 'Unknown error'}
        />
      </CardBody>
    );
  }

  const tables = query.data?.tables ?? [];

  if (tables.length === 0) {
    return (
      <CardBody>
        <EmptyState
          icon={<Database />}
          title="No tables loaded"
          description="Enable metadata loading on this data source to see its schema."
        />
      </CardBody>
    );
  }

  // Resolve the selection against the freshest snapshot — after a metadata
  // refresh the table objects are replaced, and a removed table must not keep
  // rendering from a stale reference. Falls back to the first table if gone.
  const active =
    (selected
      ? tables.find(t => t.schemaName === selected.schemaName && t.tableName === selected.tableName)
      : undefined) ?? tables[0];

  return (
    <CardBody flush className="flex flex-1 min-h-0">
      <aside className="w-[260px] shrink-0 border-r border-border overflow-x-hidden overflow-y-auto p-2">
        {tables.map(t => {
          const key = `${t.schemaName}.${t.tableName}`;
          const isActive = active && active.schemaName === t.schemaName && active.tableName === t.tableName;
          return (
            <button
              key={key}
              type="button"
              title={key}
              onClick={() => setSelected({ schemaName: t.schemaName, tableName: t.tableName })}
              className={`block w-full truncate text-left px-2.5 py-1.5 rounded-sm transition ${
                isActive ? 'bg-surface-2 font-semibold text-text' : 'text-text-muted hover:bg-surface-2 hover:text-text'
              }`}
            >
              <span className="mono text-xs">{t.schemaName}.</span>
              <span className="text-sm ml-1">{t.tableName}</span>
            </button>
          );
        })}
      </aside>

      <section className="flex-1 min-w-0 overflow-auto p-4">
        {active && <TableDetails table={active} />}
      </section>
    </CardBody>
  );
}

const COLUMN_GRID =
  'minmax(0,0.4fr) minmax(0,2fr) minmax(0,1fr) minmax(0,0.5fr) minmax(0,0.5fr) minmax(0,1.4fr)';

const COLUMN_TABLE_COLUMNS: Column<ColumnMetadataDto>[] = [
  { key: 'ord', header: '#', render: c => <span className="mono text-text-muted">{c.ordinalPosition}</span> },
  {
    key: 'name',
    header: 'Column',
    render: c => (
      <span className={c.isPrimaryKey ? 'font-semibold' : ''}>
        {c.columnName}
        {c.isPrimaryKey && <Pill className="ml-1.5">PK</Pill>}
        {c.isForeignKey && <Pill className="ml-1">FK</Pill>}
      </span>
    ),
  },
  { key: 'type', header: 'Type', render: c => <span className="mono">{c.dataType}</span> },
  { key: 'null', header: 'Null', render: c => c.isNullable ? <span className="text-text-muted">yes</span> : <span>no</span> },
  { key: 'len', header: 'Len', render: c => c.maxLength != null ? formatNumber(c.maxLength) : <span className="text-text-muted">—</span> },
  {
    key: 'ref',
    header: 'References',
    render: c => c.foreignKeyTable
      ? <span className="mono text-text-muted">{c.foreignKeyTable}.{c.foreignKeyColumn}</span>
      : <span className="text-text-muted">—</span>,
  },
];

function TableDetails({ table }: { table: TableMetadataDto }) {
  return (
    <>
      <header className="mb-3">
        <h3 className="m-0 text-sm font-semibold text-text">
          <span className="mono text-text-muted text-xs">{table.schemaName}.</span>
          {table.tableName}
        </h3>
        {table.description && (
          <div className="text-xs text-text-muted mt-1">{table.description}</div>
        )}
        <div className="text-xs text-text-muted mt-1">
          {formatNumber(table.columns.length)} columns · {formatNumber(table.indexes.length)} indexes
        </div>
      </header>

      <DataTable
        columns={COLUMN_TABLE_COLUMNS}
        rows={table.columns}
        rowKey={c => c.columnName}
        gridTemplate={COLUMN_GRID}
      />

      {table.indexes.length > 0 && (
        <section className="mt-4">
          <h4 className="m-0 mb-2 text-sm font-semibold text-text">Indexes</h4>
          <ul className="m-0 pl-4 text-sm">
            {table.indexes.map(idx => (
              <li key={idx.indexName} className="mb-1">
                <span className="mono">{idx.indexName}</span>
                {idx.isPrimaryKey && <Pill className="ml-1.5">PK</Pill>}
                {idx.isUnique && !idx.isPrimaryKey && <Pill className="ml-1.5">UNIQUE</Pill>}
                <span className="text-text-muted ml-2 text-xs">
                  ({idx.columns.join(', ')})
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}

function ConnectionTab({ entry }: { entry: DataSourceEntry }) {
  return (
    <CardBody>
      <div className="text-xs text-text-muted mb-3">
        Connection details are encrypted at rest and never returned by the API. To rotate credentials, a DataSourceAdmin replaces the connection (Replace connection); a data source the host application manages is changed in the host's code.
      </div>
      <dl className="grid grid-cols-[200px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
        <dt className="text-text-muted">Source type</dt>
        <dd><Pill>{entry.dataSourceType}</Pill></dd>
        <dt className="text-text-muted">Database engine</dt>
        <dd>{entry.databaseEngineType ? <span className="mono">{entry.databaseEngineType}</span> : <span className="text-text-muted">—</span>}</dd>
        <dt className="text-text-muted">Metadata loading</dt>
        <dd>{entry.metadataLoadingEnabled ? 'Enabled' : 'Disabled'}</dd>
        <dt className="text-text-muted">Transport</dt>
        <dd>
          {entry.transportVerified === true && <Pill tone="ok">Server verified over TLS</Pill>}
          {entry.transportVerified === false && <DataSourceStateBadges transportVerified={false} />}
          {entry.transportVerified == null && <span className="text-text-muted">—</span>}
        </dd>
      </dl>
    </CardBody>
  );
}

// A failed background refresh says why by its code; the server log has the rest.
function describeRefreshFailure(status: BackgroundTaskStatus): string {
  switch (status.failureCode) {
    case 'busy':
      return 'Another metadata refresh of this data source was running; try again when it has finished.';
    case 'data_source_disabled':
      return 'This data source is disabled; enable it to refresh its metadata.';
    case 'insecure_transport':
      return "This data source's connection does not verify the server over TLS, so its metadata was not refreshed.";
    case 'not_found':
      return 'This data source was not found.';
    case 'abandoned':
      return 'The metadata refresh stopped without finishing; try again.';
    case 'cancelled':
      return 'The metadata refresh was cancelled.';
    default:
      return 'Metadata refresh failed; the server log has the details.';
  }
}
