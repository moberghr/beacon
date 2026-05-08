import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

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
  return useMutation({
    mutationFn: ({ repoId, accessToken }: { repoId: number; accessToken: string | null }) =>
      beaconApi().updateRepositoryToken(repoId, { accessToken }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['projects'] }),
  });
}

export function useUpdateDocumentationSection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ sectionId, content }: {
      sectionId: number;
      content: string;
      projectId: number;
    }) => beaconApi().updateDocumentationSection(sectionId, { content }),
    onSuccess: (_void, vars) => {
      qc.invalidateQueries({ queryKey: ['projects', vars.projectId, 'documentation'] });
    },
  });
}
