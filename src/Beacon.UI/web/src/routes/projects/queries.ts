import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

// Local strict mirrors of the loose generated DTOs (see `unwrap` in
// '@/lib/api'). Dates are strings on the wire.

export interface ProjectSummaryEntry {
  id: number;
  name: string;
  description: string | null;
  dataSourceCount: number;
  repositoryCount: number;
  qualityScore: number | null;
  lastScanStatus: string | null;
  lastScanAt: string | null;
  createdAt: string;
}

export interface GetProjectsResult {
  entries: ProjectSummaryEntry[];
}

export interface ProjectDataSourceEntry {
  name: string;
  type: string;
  tableCount: number;
  qualityScore: number | null;
}

export interface ProjectRepositoryEntry {
  id: number;
  name: string;
  url: string;
  scanStatus: string | null;
  lastScanAt: string | null;
  referenceCount: number;
  hasAccessToken: boolean;
}

export interface ProjectDetailEntry {
  id: number;
  name: string;
  description: string | null;
  totalTables: number;
  qualityScore: number | null;
  codeReferenceCount: number;
  lastScanAt: string | null;
  dataSources: ProjectDataSourceEntry[];
  repositories: ProjectRepositoryEntry[];
  hasDocumentation: boolean;
}

export interface GetProjectDetailResult {
  project: ProjectDetailEntry | null;
}

export interface ProjectDocSectionEntry {
  id: number;
  sectionType: number;
  title: string;
  content: string;
  sortOrder: number;
}

export interface ProjectDocumentationDetailEntry {
  id: number;
  generatedAt: string;
  generatedByModel: string;
  dataSourcesAnalyzed: number;
  tablesAnalyzed: number;
  codeReferencesAnalyzed: number;
  inputTokens: number;
  outputTokens: number;
  estimatedCost: number;
  generationDuration: string;
  sections: ProjectDocSectionEntry[];
}

export interface ProjectDocumentationHistoryEntry {
  id: number;
  generatedAt: string;
  generatedByModel: string;
  tablesAnalyzed: number;
  sectionsCount: number;
  totalTokens: number;
  estimatedCost: number;
}

export interface GetProjectDocumentationResult {
  latest: ProjectDocumentationDetailEntry | null;
  history: ProjectDocumentationHistoryEntry[];
}

export function useProjectsQuery() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: async () => unwrap<GetProjectsResult>(await beaconApi().getProjects()),
  });
}

export function useProjectDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id],
    queryFn: async () =>
      unwrap<GetProjectDetailResult>(await beaconApi().getProjectDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useProjectDocumentationQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id, 'documentation'],
    queryFn: async () =>
      unwrap<GetProjectDocumentationResult>(await beaconApi().getProjectDocumentation(id as number)),
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
