import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 4 — hand-typed wrappers; replace with `beaconApi()` after
// `npm run codegen` runs against /openapi/v1.json.

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
    queryFn: () => fetchJson<GetDataSourcesResult>('/beacon/api/data-sources'),
  });
}

export function useDeleteDataSource() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      fetchJson<void>(`/beacon/api/data-sources/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: DATA_SOURCES_KEY }),
  });
}
