import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { fetchJson, unwrap } from '@/lib/api';
import { ProjectMembershipManagement, ProjectRole } from '@/lib/enums';
import { fetchPagedList } from '@/lib/paging';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';
import { useBackgroundTaskStatus } from '@/lib/useBackgroundTask';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';

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
  /** Restricted: members and Admins only. */
  requiresMembership: boolean;
  /** The caller's effective role in the project; null when it has none. */
  myRole: ProjectRole | null;
  memberCount: number;
}

export interface ProjectDataSourceEntry {
  id: number;
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
  /** The last scan completed but hit a limit (files, file size or time): the references are incomplete. */
  partialScan?: boolean;
  /** Why the last scan was partial; empty for a full scan. */
  partialScanReasons?: string[];
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
  requiresMembership: boolean;
  myRole: ProjectRole | null;
  /** The caller may manage members and link data sources, and Beacon (not the host) manages membership. */
  canManageMembers: boolean;
  /** For a caller who manages members: the project's data sources another open project also links. */
  openlyReachableDataSourceIds: number[];
  /** For an Admin: every other live project linking any of this project's data sources (empty for anyone else). */
  sharedSourceProjects: SharedSourceProjectEntry[];
  /**
   * For an Admin: per data source another live project links too, the guardrails that apply to it — the strictest of
   * the linking projects' settings — and the project that sets each (empty for anyone else).
   */
  sharedSourceGuardrails: SharedSourceGuardrailsEntry[];
  /** Who manages project memberships in this deployment: Beacon, the host, or nobody (membership unavailable). */
  membershipManagement: ProjectMembershipManagement;
}

/** A data source of the project that other live projects link too, and the guardrails that apply to it. */
export interface SharedSourceGuardrailsEntry {
  dataSourceId: number;
  dataSourceName: string;
  guardrails: SharedSourceGuardrail[];
}

/**
 * One guardrail of a shared data source: `setting` is the MCP setting's name (`enforceReadOnly`, `maxRowLimit`, …),
 * `value` its effective value as text, and the project the one that sets it (this project when it already holds it).
 */
export interface SharedSourceGuardrail {
  setting: string;
  value: string;
  projectId: number;
  projectName: string;
}

/** Another live project that links some of this project's data sources, and who administers it. */
export interface SharedSourceProjectEntry {
  projectId: number;
  name: string;
  requiresMembership: boolean;
  sharedDataSourceIds: number[];
  /** Its active project admins: display names, or "User {id}" for one without a display name. */
  projectAdminNames: string[];
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
  /** Whether the caller may edit the sections: Curate in the project (its project admin role, or an Admin). */
  canCurate?: boolean;
}

/** The projects grid: server-paged, newest first, name search in the URL. */
export function useProjectsList() {
  return usePagedList<ProjectSummaryEntry, { search: string }>({
    queryKey: ['projects'],
    path: '/beacon/api/projects',
    filters: { search: '' },
    defaultSort: { column: 'createdAt', direction: 'desc' },
  });
}

/** The only project, when exactly one exists (for pages that pre-select it); a two-row request. */
export function useSoleProject() {
  return useQuery({
    queryKey: ['projects', 'sole'],
    queryFn: async () => {
      const page = await fetchPagedList<ProjectSummaryEntry>('/beacon/api/projects', { pageSize: 2 });
      return page.totalCount === 1 ? page.items[0] : null;
    },
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

/** A document the host application imported into the project (`ExposeDocs`), without its content. */
export interface ImportedDocumentSummaryEntry {
  id: number;
  sourceKey: string;
  path: string;
  title: string;
  importedTime: string;
}

export interface ImportedDocumentContentEntry extends ImportedDocumentSummaryEntry {
  content: string;
  frontmatterJson: string | null;
}

/** The host-imported documents of a project, listed without their content. */
export function useImportedDocumentsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['projects', id, 'imported-documents'],
    queryFn: async () =>
      unwrap<{ documents: ImportedDocumentSummaryEntry[] }>(await beaconApi().getImportedDocuments(id as number)).documents,
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

/** One host-imported document with its content, read when it is opened. */
export function useImportedDocumentQuery(projectId: number, documentId: number) {
  return useQuery({
    queryKey: ['projects', projectId, 'imported-documents', documentId],
    queryFn: async () =>
      unwrap<{ document: ImportedDocumentContentEntry }>(await beaconApi().getImportedDocument(projectId, documentId)).document,
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

// ---------- Project access: members, data-source links, restriction ----------

export const PROJECT_ROLE_LABEL: Record<ProjectRole, string> = {
  [ProjectRole.Viewer]: 'Viewer',
  [ProjectRole.Editor]: 'Editor',
  [ProjectRole.ProjectAdmin]: 'Project admin',
};

export interface ProjectMemberEntry {
  userId: number;
  displayName: string;
  role: ProjectRole;
  /** False for a disabled or archived user: the row grants nothing until removed. */
  isActive: boolean;
  addedAt: string;
  modifiedAt: string | null;
  /** False when the caller may not change or remove this membership (only an Admin may). */
  canChange: boolean;
}

export interface MemberCandidate {
  userId: number;
  displayName: string;
  isMember: boolean;
  /** False when the caller may not add this user or change their membership (only an Admin may). */
  canAdd: boolean;
}

/**
 * How a user is named to other users: their display name, or "User {id}" without one. The server already sends this
 * label; the fallback only covers a blank one. Never a user name, e-mail or external id.
 */
export function userLabel(userId: number, displayName: string | null | undefined): string {
  return displayName?.trim() ? displayName : `User ${userId}`;
}

/** The server needs at least this many characters before it searches for users (by display name). */
export const MEMBER_SEARCH_MIN_LENGTH = 2;

const membersKey = (projectId: number) => ['projects', projectId, 'members'] as const;

/** A project's members: server-paged, by display name. Only for callers who manage members. */
export function useProjectMembers(projectId: number) {
  return usePagedList<ProjectMemberEntry>({
    queryKey: membersKey(projectId),
    path: `/beacon/api/projects/${projectId}/members`,
    urlPrefix: 'members',
  });
}

/** Users to add, matching `search` (at least two characters; the server returns a short list). */
export function useMemberCandidates(projectId: number, search: string) {
  const term = search.trim();
  return useQuery({
    queryKey: ['projects', projectId, 'member-candidates', term],
    queryFn: () =>
      fetchJson<{ candidates: MemberCandidate[] }>(
        `/beacon/api/projects/${projectId}/member-candidates?search=${encodeURIComponent(term)}`,
      ),
    enabled: term.length >= MEMBER_SEARCH_MIN_LENGTH,
    retry: false,
  });
}

/** Adds a member or changes a member's role. */
export function useSetProjectMember(projectId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ userId: number; role: ProjectRole }, unknown>({
      qc,
      mutationFn: ({ userId, role }) =>
        fetchJson(`/beacon/api/projects/${projectId}/members/${userId}`, {
          method: 'PUT',
          body: JSON.stringify({ role }),
        }),
      invalidate: [membersKey(projectId), ['projects', projectId, 'member-candidates'], ['projects']],
      successMsg: 'Member saved',
      errorFallback: 'Saving the member failed',
    }),
  );
}

export function useRemoveProjectMember(projectId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (userId) =>
        fetchJson(`/beacon/api/projects/${projectId}/members/${userId}`, { method: 'DELETE' }),
      invalidate: [membersKey(projectId), ['projects']],
      successMsg: 'Member removed',
      errorFallback: 'Removing the member failed',
    }),
  );
}

/** Restricts (members and Admins only) or opens a project. Admin only. */
export function useSetProjectAccess(projectId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<boolean, unknown>({
      qc,
      mutationFn: (requiresMembership) =>
        fetchJson(`/beacon/api/projects/${projectId}/access`, {
          method: 'PUT',
          body: JSON.stringify({ requiresMembership }),
        }),
      invalidate: [['projects']],
      successMsg: (requiresMembership) => (requiresMembership ? 'Project restricted' : 'Project opened'),
      errorFallback: 'Changing project access failed',
    }),
  );
}

/**
 * Removes a data source's link to a project. Admin only. A refusal (409: the links changed concurrently, or a host's
 * data source the host links) refreshes the projects as well, so the page shows the links as they are now.
 */
export function useUnlinkProjectDataSource() {
  const qc = useQueryClient();
  const options = createSimpleMutation<{ projectId: number; dataSourceId: number }, unknown>({
    qc,
    mutationFn: ({ projectId, dataSourceId }) =>
      fetchJson(`/beacon/api/projects/${projectId}/data-sources/${dataSourceId}`, { method: 'DELETE' }),
    invalidate: [['projects'], ['data-sources']],
    successMsg: 'Data source unlinked',
    errorFallback: 'Unlinking the data source failed',
  });
  return useMutation({
    ...options,
    onError: (...args) => {
      options.onError?.(...args);
      qc.invalidateQueries({ queryKey: ['projects'] });
    },
  });
}

/** Links data sources into a project. */
export function useLinkProjectDataSources(projectId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number[], unknown>({
      qc,
      mutationFn: (dataSourceIds) =>
        fetchJson(`/beacon/api/projects/${projectId}/data-sources`, {
          method: 'POST',
          body: JSON.stringify({ dataSourceIds }),
        }),
      invalidate: [['projects'], ['data-sources']],
      successMsg: (ids) => (ids.length === 1 ? 'Data source linked' : `${ids.length} data sources linked`),
      errorFallback: 'Linking data sources failed',
    }),
  );
}

/**
 * Starts a scan of every repository of the project as a background task (202 with its ticket). Its progress is followed
 * with useRepositoryScanStatus; the scan in the request (scanAllRepositories) stays for API callers.
 */
export function useStartRepositoryScan() {
  return useMutation({
    mutationFn: async (projectId: number) => unwrap<BackgroundTaskTicket>(await beaconApi().startRepositoryScan(projectId)),
  });
}

export function useRepositoryScanStatus(projectId: number, ticket: BackgroundTaskTicket | null) {
  return useBackgroundTaskStatus(['projects', projectId, 'repository-scan'] as const, ticket, async (taskId) =>
    unwrap<BackgroundTaskStatus>(await beaconApi().getRepositoryScan(projectId, taskId)),
  );
}
