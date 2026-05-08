import { useQuery } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

export interface DataCatalogEntry {
  dataSourceName: string;
  schemaName: string;
  tableName: string;
  description: string | null;
  columnCount: number;
  qualityScore: number | null;
  codeReferenceCount: number;
  dataSourceType?: number;
}

interface GetDataCatalogResult {
  entries: DataCatalogEntry[];
}

export const DATA_CATALOG_KEY = ['data-catalog'] as const;

export function useDataCatalogQuery() {
  return useQuery({
    queryKey: DATA_CATALOG_KEY,
    queryFn: () => fetchJson<GetDataCatalogResult>('/beacon/api/data-catalog'),
  });
}
