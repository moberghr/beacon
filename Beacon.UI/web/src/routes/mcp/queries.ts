import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { describeError, unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
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
  enableSampleValueCollection: boolean;
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
    queryFn: async () => unwrap<McpSettingsData>(await beaconApi().getMcpSettings()),
  });
}

export function useUpdateMcpSettings() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<McpSettingsData, void>({
      qc,
      mutationFn: (data) => beaconApi().updateMcpSettings({ data } as never),
      invalidate: [MCP_SETTINGS_KEY],
      errorFallback: 'Update MCP settings failed',
    }),
  );
}

export function useMcpTools() {
  return useQuery({
    queryKey: MCP_TOOLS_KEY,
    queryFn: async () =>
      (await beaconApi().getMcpTools()) as unknown as { toolNames: string[] },
  });
}

export function useRunMcpTool() {
  return useMutation({
    mutationFn: async (vars: {
      toolName: string;
      projectId: number;
      arguments: Record<string, unknown>;
    }) =>
      (await beaconApi().runMcpTool(vars as never)) as unknown as {
        text: string;
        isError: boolean;
      },
  });
}

export function useLearningStats() {
  return useQuery({
    queryKey: MCP_LEARNING_STATS_KEY,
    queryFn: async () =>
      unwrap<LearningStatsResult>(await beaconApi().getLearningStats(undefined)),
  });
}

export function useLearnedPatterns() {
  return useQuery({
    queryKey: MCP_PATTERNS_KEY,
    queryFn: async () =>
      (await beaconApi().getLearnedPatterns(
        undefined,
        undefined,
        undefined,
        undefined,
        undefined,
      )) as unknown as { patterns: LearnedPatternEntry[] },
  });
}

export function useDocumentationPatches() {
  return useQuery({
    queryKey: MCP_PATCHES_KEY,
    queryFn: async () =>
      (await beaconApi().getDocumentationPatches(undefined, undefined)) as unknown as {
        patches: DocumentationPatchEntry[];
      },
  });
}

export function useUpdatePatternStatus() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ patternId: number; newStatus: number }, void>({
      qc,
      mutationFn: (vars) =>
        beaconApi().updatePatternStatus(vars.patternId, {
          newStatus: vars.newStatus,
          reviewedByUserId: null,
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
      mutationFn: (id) => beaconApi().applyDocumentationPatch(id),
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
      mutationFn: (id) => beaconApi().rejectDocumentationPatch(id),
      invalidate: [MCP_PATCHES_KEY, MCP_LEARNING_STATS_KEY],
      errorFallback: 'Reject patch failed',
    }),
  );
}

export function describeMcpError(err: unknown, fallback: string): string {
  return describeError(err, fallback);
}
