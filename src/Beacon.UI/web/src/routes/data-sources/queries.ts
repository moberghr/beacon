import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { fetchJson } from '@/lib/api';
import { fetchPagedList } from '@/lib/paging';
import { usePagedList } from '@/lib/usePagedList';
import { DatabaseEngineType, type DataSourceType } from '@/lib/enums';
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
}

const DATA_SOURCES_KEY = ['data-sources'] as const;

/** The data sources grid: server-paged, alphabetical, name search in the URL. */
export function useDataSourcesList() {
  return usePagedList<DataSourceEntry, { search: string }>({
    queryKey: DATA_SOURCES_KEY,
    path: '/beacon/api/data-sources',
    filters: { search: '' },
    defaultSort: { column: 'name', direction: 'asc' },
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
 * The alphabetically first data source, for forms that pre-select one (new query, new step). A single
 * row request, not a list.
 */
export function useDefaultDataSource(options: { databaseOnly?: boolean } = {}) {
  return useQuery({
    queryKey: [...DATA_SOURCES_KEY, 'default', options],
    queryFn: async () =>
      (await fetchPagedList<DataSourceEntry>('/beacon/api/data-sources', { ...options, pageSize: 1, sort: 'name' })).items[0] ?? null,
  });
}

/** How many data sources exist (optionally database ones only) — a one-row request that reads totalCount. */
export function useDataSourceCount(options: { databaseOnly?: boolean } = {}) {
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
}

export function useCreateDataSource() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateDataSourcePayload, OperationResult>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createDataSource(values as never);
        return { success: r.success ?? false, message: r.message ?? null };
      },
      invalidate: [DATA_SOURCES_KEY],
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
