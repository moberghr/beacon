import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { describeError, fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

// Mirror enums from Beacon.Core.Data.Enums
export const McpPatternStatus = {
  Pending: 0,
  Approved: 1,
  Rejected: 2,
  AutoApproved: 3,
} as const;

export const McpPatternType = {
  CommonQuery: 0,
  TableUsage: 1,
  ColumnHint: 2,
  JoinPattern: 3,
  ValueDictionary: 4,
} as const;

export const McpDocPatchStatus = {
  Proposed: 0,
  Applied: 1,
  Rejected: 2,
} as const;

export const PATTERN_STATUS_LABEL: Record<number, string> = {
  [McpPatternStatus.Pending]: 'Pending',
  [McpPatternStatus.Approved]: 'Approved',
  [McpPatternStatus.Rejected]: 'Rejected',
  [McpPatternStatus.AutoApproved]: 'Auto-approved',
};

export const PATTERN_TYPE_LABEL: Record<number, string> = {
  [McpPatternType.CommonQuery]: 'Common query',
  [McpPatternType.TableUsage]: 'Table usage',
  [McpPatternType.ColumnHint]: 'Column hint',
  [McpPatternType.JoinPattern]: 'Join pattern',
  [McpPatternType.ValueDictionary]: 'Value dictionary',
};

export const PATCH_STATUS_LABEL: Record<number, string> = {
  [McpDocPatchStatus.Proposed]: 'Proposed',
  [McpDocPatchStatus.Applied]: 'Applied',
  [McpDocPatchStatus.Rejected]: 'Rejected',
};

export interface McpSettingsData {
  askSystemPrompt: string | null;
  globalInstruction: string | null;
  getContextDescription: string | null;
  queryDescription: string | null;
  getDocumentationDescription: string | null;
  askDescription: string | null;
  searchDescription: string | null;
  maxRowLimit: number;
  enforceReadOnly: boolean;
  enablePiiDetection: boolean;
  customPiiPatterns: string[];
  enableLearning: boolean;
  learningAutoApproveThreshold: number;
  learningInjectionBudgetChars: number;
  learningSignalRetentionDays: number;
}

export interface LearnedPatternEntry {
  id: number;
  projectId: number;
  dataSourceId: number;
  schemaName: string;
  tableName: string;
  columnName: string | null;
  patternType: number;
  patternContent: string;
  exampleQuestion: string | null;
  exampleSql: string | null;
  signalCount: number;
  confidence: number;
  status: number;
  createdTime: string;
  lastRefreshedAt: string | null;
}

export interface DocumentationPatchEntry {
  id: number;
  projectId: number;
  dataSourceId: number;
  targetType: number;
  targetIdentifier: string;
  currentContent: string | null;
  proposedContent: string;
  reasoning: string;
  supportingSignalCount: number;
  status: number;
  createdTime: string;
  appliedAt: string | null;
}

export interface ProblemTableEntry {
  tablesUsed: string;
  totalQueries: number;
  errorCount: number;
  errorRate: number;
}

export interface LearningStatsResult {
  totalSignals: number;
  signals7d: number;
  signals30d: number;
  successRate: number;
  patternsApproved: number;
  patternsPending: number;
  patternsRejected: number;
  patchesApplied: number;
  patchesProposed: number;
  problemTables: ProblemTableEntry[];
}

export const MCP_SETTINGS_KEY = ['mcp', 'settings'] as const;
export const MCP_TOOLS_KEY = ['mcp', 'tools'] as const;
export const MCP_LEARNING_STATS_KEY = ['mcp', 'learning-stats'] as const;
export const MCP_PATTERNS_KEY = ['mcp', 'patterns'] as const;
export const MCP_PATCHES_KEY = ['mcp', 'patches'] as const;

export function useMcpSettings() {
  return useQuery({
    queryKey: MCP_SETTINGS_KEY,
    queryFn: () => fetchJson<McpSettingsData>('/beacon/api/mcp/settings'),
  });
}

export function useUpdateMcpSettings() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<McpSettingsData, void>({
      qc,
      mutationFn: (data) =>
        fetchJson<void>('/beacon/api/mcp/settings', {
          method: 'PUT',
          body: JSON.stringify({ data }),
        }),
      invalidate: [MCP_SETTINGS_KEY],
      errorFallback: 'Update MCP settings failed',
    }),
  );
}

export function useMcpTools() {
  return useQuery({
    queryKey: MCP_TOOLS_KEY,
    queryFn: () => fetchJson<{ toolNames: string[] }>('/beacon/api/mcp/tools'),
  });
}

export function useRunMcpTool() {
  return useMutation({
    mutationFn: (vars: {
      toolName: string;
      projectId: number;
      arguments: Record<string, unknown>;
    }) =>
      fetchJson<{ text: string; isError: boolean }>('/beacon/api/mcp/tools/run', {
        method: 'POST',
        body: JSON.stringify(vars),
      }),
  });
}

export function useLearningStats() {
  return useQuery({
    queryKey: MCP_LEARNING_STATS_KEY,
    queryFn: () => fetchJson<LearningStatsResult>('/beacon/api/mcp/learning-stats'),
  });
}

export function useLearnedPatterns() {
  return useQuery({
    queryKey: MCP_PATTERNS_KEY,
    queryFn: () =>
      fetchJson<{ patterns: LearnedPatternEntry[] }>('/beacon/api/mcp/learned-patterns'),
  });
}

export function useDocumentationPatches() {
  return useQuery({
    queryKey: MCP_PATCHES_KEY,
    queryFn: () =>
      fetchJson<{ patches: DocumentationPatchEntry[] }>(
        '/beacon/api/mcp/documentation-patches',
      ),
  });
}

export function useUpdatePatternStatus() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ patternId: number; newStatus: number }, void>({
      qc,
      mutationFn: (vars) =>
        fetchJson<void>(`/beacon/api/mcp/learned-patterns/${vars.patternId}/status`, {
          method: 'PUT',
          body: JSON.stringify({ newStatus: vars.newStatus, reviewedByUserId: null }),
        }),
      invalidate: [MCP_PATTERNS_KEY, MCP_LEARNING_STATS_KEY],
      errorFallback: 'Update pattern status failed',
    }),
  );
}

export function useApplyPatch() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`/beacon/api/mcp/documentation-patches/${id}/apply`, {
          method: 'POST',
          body: JSON.stringify({ userId: null }),
        }),
      invalidate: [MCP_PATCHES_KEY, MCP_LEARNING_STATS_KEY],
      errorFallback: 'Apply patch failed',
    }),
  );
}

export function useRejectPatch() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`/beacon/api/mcp/documentation-patches/${id}/reject`, {
          method: 'POST',
          body: JSON.stringify({ userId: null }),
        }),
      invalidate: [MCP_PATCHES_KEY, MCP_LEARNING_STATS_KEY],
      errorFallback: 'Reject patch failed',
    }),
  );
}

export function describeMcpError(err: unknown, fallback: string): string {
  return describeError(err, fallback);
}
