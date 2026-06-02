import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

export function useProjectsQuery() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => beaconApi().getProjects(),
  });
}

export function useProjectDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id],
    queryFn: () => beaconApi().getProjectDetail(id as number),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useProjectDocumentationQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id, 'documentation'],
    queryFn: () => beaconApi().getProjectDocumentation(id as number),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useUpdateRepositoryToken() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ repoId: number; accessToken: string | null }, unknown>({
      qc,
      mutationFn: ({ repoId, accessToken }) =>
        beaconApi().updateRepositoryToken(repoId, { accessToken }),
      invalidate: [['projects']],
      errorFallback: 'Update repository token failed',
    }),
  );
}

export function useUpdateDocumentationSection() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ sectionId: number; content: string; projectId: number }, unknown>({
      qc,
      mutationFn: ({ sectionId, content }) =>
        beaconApi().updateDocumentationSection(sectionId, { content }),
      invalidate: (vars) => [['projects', vars.projectId, 'documentation']],
      errorFallback: 'Update documentation section failed',
    }),
  );
}
