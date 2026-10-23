import { useMutation, useQueries, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { fetchJson } from '@/lib/api';
import { fetchPagedList } from '@/lib/paging';
import { usePagedList } from '@/lib/usePagedList';
import { DatabaseEngineType, type ChangeApprovalKind, type DataSourceType } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const DATABASE_ENGINE_LABEL: Record<DatabaseEngineType, string> = {
  [DatabaseEngineType.PostgreSQL]: 'PostgreSQL',
  [DatabaseEngineType.MSSQL]: 'SQL Server',
  [DatabaseEngineType.MySQL]: 'MySQL',
  [DatabaseEngineType.SQLite]: 'SQLite',
  [DatabaseEngineType.AzureSynapse]: 'Azure Synapse',
  [DatabaseEngineType.Snowflake]: 'Snowflake',
};

export interface DataSourceEntry {
  id: number;
  name: string;
  dataSourceType: string;
  databaseEngineType: string | null;
  queryCount: number;
  migrationJobsCount: number;
  metadataLoadingEnabled: boolean;
  /** Reachable without a project link (sources that existed before project access); new sources are not. */
  openWhenUnlinked: boolean;
  /**
   * An Admin marked it as a migration write target: migrations may write to it and Beacon never reads it, so its login may
   * hold write grants. Every other data source should use a login that can only read.
   */
  allowMigrationWrites?: boolean;
  /** The live projects linking the source that the caller may read. */
  projects: DataSourceProjectChip[];
  /** Live projects linking the source that the caller may not read. */
  otherProjectCount: number;
  /**
   * True when the engine refuses writes in Beacon's read-only session (PostgreSQL, MySQL); false when Beacon's parser
   * checks are the only guard, so the source must be connected with a login that can only read; null when it runs no SQL.
   */
  databaseReadOnlyEnforced?: boolean | null;
  /** A DataSourceAdmin disabled it: Beacon makes no connection with it, so every run, query and MCP call on it fails. */
  disabled?: boolean;
  disabledTime?: string | null;
  /**
   * False when its connection does not authenticate the server over TLS ("transport not verified"); true when it does;
   * null when the connection data cannot be read.
   */
  transportVerified?: boolean | null;
  /** The stored settings an edit sends back unchanged, and whether the host manages the source. By-id read only. */
  editSettings?: DataSourceEditSettings | null;
  /**
   * Its connection change waiting for a second person's approval (it connects to nothing meanwhile): shown to an Approver
   * and to whoever submitted it; null for everyone else and when nothing waits.
   */
  approval?: DataSourceApproval | null;
  /**
   * True while connections are covered by approval and the source awaits its first approval, a request pending or not (a
   * rejected one leaves none). Visible to every reader; it connects to nothing until its connection is saved and approved.
   */
  awaitsFirstApproval?: boolean;
}

/** A data source's pending connection change. */
export interface DataSourceApproval {
  requestId: number;
  kind: ChangeApprovalKind;
  requestedTime: string;
  /** A new source that was never approved; false for a change of a source approved before. */
  awaitsFirstApproval: boolean;
}

/** A data source's stored metadata-loading options (never its connection), and whether the host application manages it. */
export interface DataSourceEditSettings {
  hostManaged?: boolean;
  metadataMaxTables?: number;
  metadataMaxColumnsPerTable?: number;
  metadataLoadTableNamesOnly?: boolean;
  /** Comma-separated, as stored. */
  metadataExcludeSchemas?: string | null;
  /** Comma-separated, as stored. */
  metadataIncludeSchemas?: string | null;
}

/** A project linking a data source. */
export interface DataSourceProjectChip {
  id: number;
  name: string;
}

const DATA_SOURCES_KEY = ['data-sources'] as const;

/** The data sources grid: server-paged, alphabetical, name search in the URL. */
export function useDataSourcesList(enabled = true) {
  return usePagedList<DataSourceEntry, { search: string }>({
    queryKey: DATA_SOURCES_KEY,
    path: '/beacon/api/data-sources',
    filters: { search: '' },
    defaultSort: { column: 'name', direction: 'asc' },
    enabled,
  });
}

/** One data source by id — for detail pages and for labelling a picker's current value. */
export function useDataSourceQuery(id: number | null | undefined) {
  return useQuery({
    queryKey: [...DATA_SOURCES_KEY, 'by-id', id],
    queryFn: () => fetchJson<DataSourceEntry>(`/beacon/api/data-sources/${id}`),
    enabled: typeof id === 'number' && id > 0,
  });
}

/**
 * The data source as the server holds it right now (past the cache), and the cache is primed with it. Read after a save
 * to learn whether the change was held for approval, which the save's own answer (204) does not say.
 */
export function fetchDataSourceNow(qc: QueryClient, id: number) {
  return qc.fetchQuery({
    queryKey: [...DATA_SOURCES_KEY, 'by-id', id],
    queryFn: () => fetchJson<DataSourceEntry>(`/beacon/api/data-sources/${id}`),
    staleTime: 0,
  });
}

/**
 * The alphabetically first data source Beacon reads (never a migration write target), for forms that pre-select one (new
 * query, new step). A single row request, not a list.
 */
export function useDefaultDataSource(options: { databaseOnly?: boolean } = {}) {
  return useQuery({
    queryKey: [...DATA_SOURCES_KEY, 'default', options],
    queryFn: async () =>
      (await fetchPagedList<DataSourceEntry>('/beacon/api/data-sources', {
        ...options,
        migrationTargets: false,
        pageSize: 1,
        sort: 'name',
      })).items[0] ?? null,
  });
}

/**
 * How many data sources exist (optionally database ones only, or only migration write targets / only read sources) — a
 * one-row request that reads totalCount.
 */
export function useDataSourceCount(options: { databaseOnly?: boolean; migrationTargets?: boolean } = {}) {
  return useQuery({
    queryKey: [...DATA_SOURCES_KEY, 'count', options],
    queryFn: async () =>
      (await fetchPagedList<DataSourceEntry>('/beacon/api/data-sources', { ...options, pageSize: 1 })).totalCount,
  });
}

/**
 * Name and engine for a handful of data sources by id (the ones a query's steps use), fetched one by
 * one and shared with the pickers' by-id cache — not the whole list.
 */
export function useDataSourceLookup(ids: number[]) {
  const unique = [...new Set(ids.filter(x => x > 0))].sort((a, b) => a - b);
  const results = useQueries({
    queries: unique.map(id => ({
      queryKey: [...DATA_SOURCES_KEY, 'by-id', id],
      queryFn: () => fetchJson<DataSourceEntry>(`/beacon/api/data-sources/${id}`),
      staleTime: 60_000,
    })),
  });
  const map = new Map<number, { name: string; engine: string }>();
  for (const result of results) {
    if (result.data) {
      map.set(result.data.id, { name: result.data.name, engine: dataSourceEngine(result.data) });
    }
  }
  return map;
}

export function dataSourceEngine(entry: Pick<DataSourceEntry, 'databaseEngineType' | 'dataSourceType'>): string {
  return entry.databaseEngineType ?? entry.dataSourceType;
}

export interface CreateDataSourcePayload {
  name: string;
  dataSourceType: DataSourceType;
  databaseEngineType: DatabaseEngineType | null;
  connectionString: string;
  metadataLoadingEnabled: boolean;
  metadataMaxTables: number;
  metadataMaxColumnsPerTable: number;
  metadataLoadTableNamesOnly: boolean;
  metadataExcludeSchemas: string[];
  metadataIncludeSchemas: string[];
  /** Projects to link the new source into; without one, only Admins can use it until it is linked. */
  projectIds: number[];
}

export interface TestDataSourceConnectionPayload {
  name: string | null;
  dataSourceType: DataSourceType;
  databaseEngineType: DatabaseEngineType | null;
  connectionString: string;
}

export interface OperationResult {
  success: boolean;
  message: string | null;
  /** The server saved the new source but holds it: it connects to nothing until a second person approves it. */
  awaitingApproval?: boolean;
}

export function useCreateDataSource() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateDataSourcePayload, OperationResult>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createDataSource(values as never);
        return { success: r.success ?? false, message: r.message ?? null, awaitingApproval: r.awaitingApproval === true };
      },
      invalidate: [DATA_SOURCES_KEY, ['projects']],
      errorFallback: 'Create data source failed',
    }),
  );
}

export function useTestDataSourceConnection() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<TestDataSourceConnectionPayload, OperationResult>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().testDataSourceConnection(values as never);
        return { success: r.success ?? false, message: r.message ?? null };
      },
      errorFallback: 'Connection test failed',
    }),
  );
}

export interface UpdateDataSourcePayload {
  id: number;
  name: string;
  databaseEngineType: DatabaseEngineType | null;
  /** Null keeps the stored connection, which is never shown or sent back. */
  connectionString: string | null;
  /** The metadata settings are optional: omitted (null) keeps the stored value. */
  metadataLoadingEnabled?: boolean | null;
  metadataMaxTables?: number | null;
  metadataMaxColumnsPerTable?: number | null;
  metadataLoadTableNamesOnly?: boolean | null;
  metadataExcludeSchemas?: string[] | null;
  metadataIncludeSchemas?: string[] | null;
  /** Confirms a connection to another engine, host, port or database (the server answers 409 without it). */
  confirmTargetChange: boolean;
}

/**
 * Edits a data source and replaces its connection (credential rotation). A connection pointing somewhere else answers 409
 * until `confirmTargetChange` is sent, and a refused connection answers 400 naming the option, so the caller shows the
 * server's reason itself (no toast here).
 */
export function useUpdateDataSource() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<UpdateDataSourcePayload, void>({
      qc,
      mutationFn: ({ id, ...body }) => beaconApi().updateDataSource(id, body as never),
      invalidate: [DATA_SOURCES_KEY],
      errorFallback: 'Replacing the connection failed',
      errorToast: false,
    }),
  );
}

/** Disables a data source (Beacon makes no connection with it) or enables it again. DataSourceAdmin only. */
export function useSetDataSourceEnabled() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; enabled: boolean }, void>({
      qc,
      mutationFn: ({ id, enabled }) => beaconApi().setDataSourceEnabled(id, { enabled }),
      invalidate: [DATA_SOURCES_KEY],
      errorFallback: 'Changing whether the data source is enabled failed',
    }),
  );
}

// ---------- Metadata (schema explorer + Monaco autocomplete) -----------------

export interface ColumnMetadataDto {
  columnName: string;
  dataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
  isForeignKey: boolean;
  ordinalPosition: number;
  foreignKeyTable: string | null;
  foreignKeyColumn: string | null;
  defaultValue: string | null;
  maxLength: number | null;
  description: string | null;
}

export interface IndexMetadataDto {
  indexName: string;
  isUnique: boolean;
  isPrimaryKey: boolean;
  columns: string[];
}

export interface TableMetadataDto {
  schemaName: string;
  tableName: string;
  columns: ColumnMetadataDto[];
  indexes: IndexMetadataDto[];
  description: string | null;
}

export interface DatabaseMetadataSnapshot {
  dataSourceId: number;
  databaseEngineType: string | null;
  tables: TableMetadataDto[];
  refreshedAt: string;
  /** Names the refresh that read this snapshot left out (quote, bracket, semicolon or control characters). */
  skippedNameCount?: number;
}

export function useDataSourceMetadataQuery(dataSourceId: number | null | undefined) {
  return useQuery({
    queryKey: ['data-sources', dataSourceId, 'metadata'] as const,
    queryFn: async () =>
      unwrap<DatabaseMetadataSnapshot>(await beaconApi().getDataSourceMetadata(dataSourceId as number)),
    enabled: dataSourceId != null && dataSourceId > 0,
    staleTime: 60_000,
    retry: false,
  });
}

export function useRefreshDataSourceMetadata() {
  const qc = useQueryClient();
  // Keeps `qc.setQueryData` semantics (cache write, not invalidate) — outside
  // the createSimpleMutation factory which only invalidates.
  return useMutation({
    mutationFn: async (dataSourceId: number) =>
      unwrap<DatabaseMetadataSnapshot>(await beaconApi().refreshDataSourceMetadata(dataSourceId)),
    onSuccess: (snapshot, dataSourceId) => {
      qc.setQueryData(['data-sources', dataSourceId, 'metadata'] as const, snapshot);
    },
  });
}

export function useDeleteDataSource() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().deleteDataSource(id),
      invalidate: [DATA_SOURCES_KEY],
      errorFallback: 'Delete data source failed',
    }),
  );
}

/**
 * Marks a data source as a migration write target, or returns it to an ordinary read source (Admin only). Marking a
 * source that queries, subscriptions or data contracts read answers 409 with how many unless `force` confirms it, so
 * the caller handles errors itself (no toast here).
 */
export function useSetMigrationWrites() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; allowMigrationWrites: boolean; force?: boolean }, void>({
      qc,
      mutationFn: ({ id, allowMigrationWrites, force }) =>
        beaconApi().setDataSourceMigrationWrites(id, force ? { allowMigrationWrites, force } : { allowMigrationWrites }),
      invalidate: [DATA_SOURCES_KEY],
      errorFallback: 'Changing the migration write target failed',
      errorToast: false,
    }),
  );
}
