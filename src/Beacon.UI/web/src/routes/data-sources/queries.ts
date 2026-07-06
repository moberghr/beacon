import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
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

interface GetDataSourcesResult {
  entries: DataSourceEntry[];
}

const DATA_SOURCES_KEY = ['data-sources'] as const;

export function useDataSourcesQuery() {
  return useQuery({
    queryKey: DATA_SOURCES_KEY,
    queryFn: async () =>
      unwrap<GetDataSourcesResult>(await beaconApi().getDataSources()),
  });
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
