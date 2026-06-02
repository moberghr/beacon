import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

// DataSourceType / DatabaseEngineType values mirror Beacon.Core.Data.Enums.
// Backend serialises enums as ints by default — keep these constants aligned.
export const DATA_SOURCE_TYPE = {
  Database: 1,
  CloudWatch: 2,
  Databricks: 6,
  BigQuery: 7,
  Api: 8,
} as const;
export type DataSourceTypeId = typeof DATA_SOURCE_TYPE[keyof typeof DATA_SOURCE_TYPE];

export const DATABASE_ENGINE = {
  PostgreSQL: 1,
  MSSQL: 2,
  MySQL: 3,
  SQLite: 4,
  AzureSynapse: 5,
  Snowflake: 6,
} as const;
export type DatabaseEngineId = typeof DATABASE_ENGINE[keyof typeof DATABASE_ENGINE];

export const DATABASE_ENGINE_LABEL: Record<DatabaseEngineId, string> = {
  [DATABASE_ENGINE.PostgreSQL]: 'PostgreSQL',
  [DATABASE_ENGINE.MSSQL]: 'SQL Server',
  [DATABASE_ENGINE.MySQL]: 'MySQL',
  [DATABASE_ENGINE.SQLite]: 'SQLite',
  [DATABASE_ENGINE.AzureSynapse]: 'Azure Synapse',
  [DATABASE_ENGINE.Snowflake]: 'Snowflake',
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

interface GetDataSourcesResult {
  entries: DataSourceEntry[];
}

const DATA_SOURCES_KEY = ['data-sources'] as const;

export function useDataSourcesQuery() {
  return useQuery({
    queryKey: DATA_SOURCES_KEY,
    queryFn: async () =>
      (await beaconApi().getDataSources()) as unknown as GetDataSourcesResult,
  });
}

export interface CreateDataSourcePayload {
  name: string;
  dataSourceType: DataSourceTypeId;
  databaseEngineType: DatabaseEngineId | null;
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
  dataSourceType: DataSourceTypeId;
  databaseEngineType: DatabaseEngineId | null;
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
      (await beaconApi().getDataSourceMetadata(dataSourceId as number)) as unknown as DatabaseMetadataSnapshot,
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
      (await beaconApi().refreshDataSourceMetadata(dataSourceId)) as unknown as DatabaseMetadataSnapshot,
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
