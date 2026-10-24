import { useMutation, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';

/** Wire-aligned with `QueryFolderData`. `queryCount` covers only the queries the caller may read. */
export interface QueryFolderEntry {
  id: number;
  name: string;
  description: string | null;
  parentFolderId: number | null;
  path: string;
  sortOrder: number;
  queryCount: number;
  childFolderCount: number;
}

const FOLDERS_KEY = ['query-folders'] as const;

/** The folders grid: server-paged, in tree order; search (name, path) in the URL. */
export function useQueryFoldersList() {
  return usePagedList<QueryFolderEntry, { search: string }>({
    queryKey: FOLDERS_KEY,
    path: '/beacon/api/query-folders/paged',
    filters: { search: '' },
  });
}

export interface QueryFolderFormValues {
  name: string;
  description: string | null;
  parentFolderId: number | null;
}

export function useCreateQueryFolder() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<QueryFolderFormValues, number>({
      qc,
      mutationFn: async (values) => {
        const r = await beaconApi().createQueryFolder(values as never);
        return r.folderId ?? 0;
      },
      invalidate: [FOLDERS_KEY],
      errorFallback: 'Create folder failed',
    }),
  );
}

export function useUpdateQueryFolder() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; name: string; description: string | null }, void>({
      qc,
      mutationFn: ({ id, name, description }) => beaconApi().updateQueryFolder(id, { name, description } as never),
      invalidate: [FOLDERS_KEY, ['queries']],
      errorFallback: 'Rename folder failed',
    }),
  );
}

export function useDeleteQueryFolder() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; moveQueriesToParent: boolean }, void>({
      qc,
      mutationFn: ({ id, moveQueriesToParent }) => beaconApi().deleteQueryFolder(id, moveQueriesToParent),
      invalidate: [FOLDERS_KEY, ['queries']],
      errorFallback: 'Delete folder failed',
    }),
  );
}

/** Moves a query into a folder, or out of any folder when `folderId` is null. */
export function useMoveQueryToFolder() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ queryId: number; folderId: number | null }, void>({
      qc,
      mutationFn: ({ queryId, folderId }) => beaconApi().moveQueryToFolder(queryId, { folderId } as never),
      invalidate: [FOLDERS_KEY, ['queries']],
      errorFallback: 'Move query failed',
    }),
  );
}
