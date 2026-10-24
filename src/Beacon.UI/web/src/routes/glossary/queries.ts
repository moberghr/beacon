import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';

/** Wire-aligned with `GlossaryTermItem`; dates arrive as strings. */
export interface GlossaryTermEntry {
  id: number;
  projectId: number;
  dataSourceId: number | null;
  term: string;
  synonyms: string | null;
  definition: string;
  targetSchema: string | null;
  targetTable: string | null;
  targetColumn: string | null;
  metricExpression: string | null;
  isActive: boolean;
  createdTime: string;
}

const GLOSSARY_KEY = ['glossary'] as const;

/**
 * A project's glossary: server-paged, by term; the project, the search and "include inactive" live in the URL.
 * Nothing loads until a project is chosen (terms belong to one project).
 */
export function useGlossaryList() {
  const [searchParams] = useSearchParams();
  const list = usePagedList<GlossaryTermEntry, { projectId: string; search: string; inactive: string }>({
    queryKey: GLOSSARY_KEY,
    path: '/beacon/api/glossary/paged',
    enabled: (searchParams.get('projectId') ?? '') !== '',
    filters: { projectId: '', search: '', inactive: '' },
    mapFilters: f => ({
      projectId: f.projectId === '' ? undefined : Number(f.projectId),
      search: f.search === '' ? undefined : f.search,
      includeInactive: f.inactive === 'true' ? true : undefined,
    }),
  });
  return list;
}

export interface GlossaryTermFormValues {
  projectId: number;
  dataSourceId: number | null;
  term: string;
  synonyms: string | null;
  definition: string;
  targetSchema: string | null;
  targetTable: string | null;
  targetColumn: string | null;
  metricExpression: string | null;
}

export function useCreateGlossaryTerm() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<GlossaryTermFormValues, number>({
      qc,
      mutationFn: async (values) => (await beaconApi().createGlossaryTerm(values as never)).id ?? 0,
      invalidate: [GLOSSARY_KEY],
      errorFallback: 'Create glossary term failed',
    }),
  );
}

export type GlossaryTermUpdate = Omit<GlossaryTermFormValues, 'projectId'> & { isActive: boolean };

export function useUpdateGlossaryTerm() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; values: GlossaryTermUpdate }, void>({
      qc,
      mutationFn: ({ id, values }) => beaconApi().updateGlossaryTerm(id, values as never),
      invalidate: [GLOSSARY_KEY],
      errorFallback: 'Update glossary term failed',
    }),
  );
}

export function useDeleteGlossaryTerm() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().deleteGlossaryTerm(id),
      invalidate: [GLOSSARY_KEY],
      errorFallback: 'Delete glossary term failed',
    }),
  );
}
