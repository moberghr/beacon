import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { toast } from 'sonner';
import { AlertTriangle, Database, Key, Layers, RefreshCw, X } from 'lucide-react';
import {
  PageHeader,
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
import { describeError } from '@/lib/api';
import { formatNumber } from '@/lib/format';
import {
  useDataSourcesQuery,
  useDataSourceMetadataQuery,
  useDeleteDataSource,
  useRefreshDataSourceMetadata,
  type ColumnMetadataDto,
  type TableMetadataDto,
} from './queries';

type TabKey = 'overview' | 'schema' | 'connection';

export default function DataSourceDetailPage() {
  const { id } = useParams();
  const numericId = id ? Number.parseInt(id, 10) : Number.NaN;
  const navigate = useNavigate();

  const listQuery = useDataSourcesQuery();
  const metadataQuery = useDataSourceMetadataQuery(Number.isFinite(numericId) ? numericId : null);
  const deleteMutation = useDeleteDataSource();
  const refreshMetadata = useRefreshDataSourceMetadata();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [tab, setTab] = useState<TabKey>('overview');

  const entry = useMemo(
    () => listQuery.data?.entries.find(x => x.id === numericId) ?? null,
    [listQuery.data, numericId],
  );

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

  if (!entry) {
    return (
      <div className="flex flex-col gap-5 p-7">
        <PageHeader
          variant="nodes"
          emphasis={`Data source #${numericId}`}
          sub={<Link to="/data-sources" className="text-text-muted">← back to data sources</Link>}
        />
        <EmptyState icon={<Database />} title="Data source not found" />
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

  const tableCount = metadataQuery.data?.tables.length ?? 0;
  const columnCount = metadataQuery.data?.tables.reduce((acc, t) => acc + t.columns.length, 0) ?? 0;

  const tabs: TabDef<TabKey>[] = [
    { key: 'overview', label: <><Layers className="size-3.5" /> Overview</> },
    { key: 'schema', label: <><Database className="size-3.5" /> Schema</>, count: tableCount },
    { key: 'connection', label: <><Key className="size-3.5" /> Connection</> },
  ];

  return (
    <div className="flex flex-col gap-5 p-7">
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
              onClick={async () => {
                try {
                  await refreshMetadata.mutateAsync(entry.id);
                  toast.success('Metadata refreshed from source');
                } catch (err) {
                                    toast.error(describeError(err, 'Refresh failed'));
                }
              }}
              disabled={refreshMetadata.isPending || metadataQuery.isFetching}
              icon={<RefreshCw />}
            >
              Refresh metadata
            </Button>
            <Button
              variant="danger"
              type="button"
              onClick={() => setConfirmDelete(true)}
              icon={<X />}
            >
              Delete
            </Button>
          </>
        }
      />

      <KPIGrid>
        <KPI dot="brand" label="Type" value={entry.dataSourceType} sub={entry.databaseEngineType ?? '—'} />
        <KPI dot="info" label="Tables" value={formatNumber(tableCount)} sub="from metadata" />
        <KPI dot="ok" label="Columns" value={formatNumber(columnCount)} sub="across all tables" />
        <KPI dot="warn" label="Queries" value={formatNumber(entry.queryCount)} sub={`${formatNumber(entry.migrationJobsCount)} migrations`} />
      </KPIGrid>

      <Card>
        <Tabs tabs={tabs} active={tab} onChange={setTab} />

        {tab === 'overview' && <OverviewTab entry={entry} metadataLoading={metadataQuery.isLoading} />}
        {tab === 'schema' && <SchemaTab query={metadataQuery} />}
        {tab === 'connection' && <ConnectionTab entry={entry} />}
      </Card>

      <ConfirmDialog
        open={confirmDelete}
        title="Delete data source"
        message={
          <>Delete data source <strong>{entry.name}</strong>? This removes related queries and migrations. This cannot be undone.</>
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
  entry: { id: number; name: string; dataSourceType: string; databaseEngineType: string | null; queryCount: number; migrationJobsCount: number; metadataLoadingEnabled: boolean };
  metadataLoading: boolean;
}

function OverviewTab({ entry, metadataLoading }: OverviewProps) {
  return (
    <CardBody>
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
        <dt className="text-text-muted">Metadata loading</dt>
        <dd>{entry.metadataLoadingEnabled
          ? <Pill tone="ok">Enabled</Pill>
          : <span className="text-text-muted">Disabled</span>}</dd>
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
    <CardBody flush className="grid grid-cols-[260px_1fr] gap-4">
      <aside className="border-r border-border max-h-[480px] overflow-y-auto p-2">
        {tables.map(t => {
          const key = `${t.schemaName}.${t.tableName}`;
          const isActive = active && active.schemaName === t.schemaName && active.tableName === t.tableName;
          return (
            <button
              key={key}
              type="button"
              onClick={() => setSelected({ schemaName: t.schemaName, tableName: t.tableName })}
              className={`w-full text-left px-2.5 py-1.5 rounded-sm transition flex items-center gap-1.5 ${
                isActive ? 'bg-surface-2 font-semibold text-text' : 'text-text-muted hover:bg-surface-2 hover:text-text'
              }`}
            >
              <span className="mono text-xs">{t.schemaName}.</span>
              <span className="text-sm">{t.tableName}</span>
            </button>
          );
        })}
      </aside>

      <section className="p-4 overflow-auto">
        {active && <TableDetails table={active} />}
      </section>
    </CardBody>
  );
}

const COLUMN_GRID = '0.6fr 1.6fr 1fr 0.5fr 0.5fr 1fr';

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

function ConnectionTab({ entry }: { entry: { dataSourceType: string; databaseEngineType: string | null; metadataLoadingEnabled: boolean } }) {
  return (
    <CardBody>
      <div className="text-xs text-text-muted mb-3">
        Connection details are encrypted at rest and not returned by the API. To rotate credentials, delete the data source and re-create it.
      </div>
      <dl className="grid grid-cols-[200px_1fr] gap-x-3 gap-y-2 m-0 text-sm">
        <dt className="text-text-muted">Source type</dt>
        <dd><Pill>{entry.dataSourceType}</Pill></dd>
        <dt className="text-text-muted">Database engine</dt>
        <dd>{entry.databaseEngineType ? <span className="mono">{entry.databaseEngineType}</span> : <span className="text-text-muted">—</span>}</dd>
        <dt className="text-text-muted">Metadata loading</dt>
        <dd>{entry.metadataLoadingEnabled ? 'Enabled' : 'Disabled'}</dd>
      </dl>
    </CardBody>
  );
}
