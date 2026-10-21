import { useMutation, useQuery, useQueryClient, type QueryClient, type QueryKey } from '@tanstack/react-query';
import { toast } from 'sonner';
import { describeError, httpErrorInfo, unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { GroundingRecheckKind, McpDocPatchStatus, McpDocPatchTarget, McpPatternStatus, McpPatternType } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

/**
 * What a review answers when what was reviewed changed meanwhile (`409`: a pattern's content, a candidate's rating or SQL):
 * the decision was not made, and the list is read again.
 */
export const CHANGED_SINCE_REVIEWED = 'This changed since you reviewed it — reload and review again.';

/** True for the `409` a review gets when the pattern or candidate changed since the reviewer saw it. */
export function isChangedSinceReviewed(err: unknown): boolean {
  return httpErrorInfo(err)?.status === 409;
}

/** Reports a failed review once: a `409` as {@link CHANGED_SINCE_REVIEWED} (and reads `reload` again), anything else with the server's reason. */
function reportReviewError(qc: QueryClient, err: unknown, fallback: string, reload: QueryKey[]) {
  if (isChangedSinceReviewed(err)) {
    toast.error(CHANGED_SINCE_REVIEWED);
    for (const key of reload) {
      qc.invalidateQueries({ queryKey: key });
    }
    return;
  }
  toast.error(describeError(err, fallback));
}

export const PATTERN_STATUS_LABEL: Record<number, string> = {
  [McpPatternStatus.Pending]: 'Pending',
  [McpPatternStatus.Approved]: 'Approved',
  [McpPatternStatus.Rejected]: 'Rejected',
  [McpPatternStatus.AutoApproved]: 'Auto-approved',
  [McpPatternStatus.NeedsEvidence]: 'Needs evidence',
};

/**
 * The statuses a reviewer may set on a learned pattern — the server refuses any other (`400`); auto-approval and
 * "needs evidence" are the learning job's. Each is offered unless the pattern already has it.
 */
export const PATTERN_REVIEW_ACTIONS: { status: McpPatternStatus; label: string; done: string }[] = [
  { status: McpPatternStatus.Approved, label: 'Approve', done: 'Pattern approved.' },
  { status: McpPatternStatus.Rejected, label: 'Reject', done: 'Pattern rejected.' },
  { status: McpPatternStatus.Pending, label: 'Back to pending', done: 'Pattern set back to pending.' },
];

/** Why a pattern's review actions are not offered: reviewing needs Curate (the project admin role) in its project. */
export const PATTERN_CURATE_REASON = 'Only a project admin of its project (or an Admin) reviews a pattern.';

export const PATCH_TARGET_LABEL: Record<number, string> = {
  [McpDocPatchTarget.ColumnDescription]: 'Column description',
  [McpDocPatchTarget.TableDescription]: 'Table description',
  [McpDocPatchTarget.DocumentationSection]: 'Documentation section',
};

/**
 * Why a patch's apply/reject are not offered. A section patch changes its project's documentation; a description patch
 * changes the data source's shared metadata, which every project linking the source is grounded on.
 */
export function patchCurateReason(targetType: number): string {
  return targetType === McpDocPatchTarget.DocumentationSection
    ? 'Only a project admin of its project (or an Admin) applies or rejects this patch.'
    : 'Only a project admin of every project linking its data source, a DataSourceAdmin or an Admin applies or rejects this patch.';
}

export const PATTERN_TYPE_LABEL: Record<number, string> = {
  [McpPatternType.CommonQuery]: 'Common query',
  [McpPatternType.ColumnClarification]: 'Column clarification',
  [McpPatternType.JoinPattern]: 'Join pattern',
  [McpPatternType.SchemaCorrection]: 'Schema correction',
  [McpPatternType.BusinessTermMapping]: 'Business term mapping',
  [McpPatternType.DocumentationGap]: 'Documentation gap',
};

export const PATCH_STATUS_LABEL: Record<number, string> = {
  [McpDocPatchStatus.Proposed]: 'Proposed',
  [McpDocPatchStatus.Applied]: 'Applied',
  [McpDocPatchStatus.Rejected]: 'Rejected',
};

/**
 * The global settings the page edits. The server's row has more (self-consistency, retrieval, exemplar and grounding
 * budgets…): a save spreads everything it loaded, so the settings the page does not show go back as they were read.
 */
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
  enableValueGrounding: boolean;
  enableLearning: boolean;
  learningAutoApproveThreshold: number;
  learningInjectionBudgetChars: number;
  learningSignalRetentionDays: number;
  retainQueryContent: boolean;
  statementTimeoutSeconds: number;
  maxResultBytes: number;
  maxExplainCost: number | null;
  maxConcurrentQueriesPerKey: number;
  allowExplicitFeedbackContent: boolean;
}

/**
 * Per-project overrides. Every member is nullable: `null` (or absent) means "inherit
 * the global value". Mirrors the server DTO field-for-field so a save can round-trip
 * overrides the settings page does not edit without clearing them.
 */
export interface McpProjectSettingsData {
  maxRowLimit?: number | null;
  enforceReadOnly?: boolean | null;
  enablePiiDetection?: boolean | null;
  customPiiPatterns?: string[] | null;
  enableSampleValueCollection?: boolean | null;
  enableLearning?: boolean | null;
  learningAutoApproveThreshold?: number | null;
  learningInjectionBudgetChars?: number | null;
  learningSignalRetentionDays?: number | null;
  enableSelfConsistency?: boolean | null;
  selfConsistencyCandidateCount?: number | null;
  enableEvalJudge?: boolean | null;
  enableSemanticRetrieval?: boolean | null;
  exemplarTopK?: number | null;
  enableReplayVerification?: boolean | null;
  learningReplayMinFlips?: number | null;
  enableContextualRetrieval?: boolean | null;
  docChunkWindowSentences?: number | null;
  docChunkOverlapSentences?: number | null;
  glossaryTopK?: number | null;
  docChunkTopK?: number | null;
  enableGoldenExemplars?: boolean | null;
  goldenExemplarTopK?: number | null;
  goldenExemplarBudgetChars?: number | null;
  enableValueGrounding?: boolean | null;
  valueGroundingMaxProbes?: number | null;
  enableSemanticLint?: boolean | null;
  selfConsistencyMinTables?: number | null;
  retainQueryContent?: boolean | null;
  statementTimeoutSeconds?: number | null;
  maxResultBytes?: number | null;
  maxExplainCost?: number | null;
  maxConcurrentQueriesPerKey?: number | null;
  allowExplicitFeedbackContent?: boolean | null;
}

export interface GetMcpProjectSettingsResult {
  projectId: number;
  overrides: McpProjectSettingsData;
  effective: McpSettingsData;
  /** Field names (PascalCase, as the server reports them) pinned by a `Beacon:Mcp` deployment lock. */
  lockedFields: string[];
  /** Field names whose effective value was clamped down to a `Beacon:Mcp:Ceilings` value. */
  clampedFields: string[];
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
  /** Who last set the review status (display name or "User {id}"); null when no person reviewed it. */
  reviewedByName: string | null;
  reviewedAt: string | null;
  /** Whether the caller may approve, reject or reset the pattern (the project admin role in its project). */
  canCurate: boolean;
  /** SHA-256 of what a reviewer decides on (content, example question, example SQL); a review sends it back. */
  contentHash: string;
  /** True when prompts leave the example SQL out (a comment that cannot be removed, or characters that do not show). */
  exampleSqlWithheld?: boolean;
  /** True when prompts leave the example question out (characters that do not show). */
  exampleQuestionWithheld?: boolean;
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
  /** When the patch was applied or rejected. */
  appliedAt: string | null;
  /** Who applied or rejected the patch (display name or "User {id}"). */
  appliedByName: string | null;
  /** Whether the caller may apply or reject the patch (see `patchCurateReason`). */
  canCurate: boolean;
}

/** `GET /eval/golden/candidates` (Admin): an answer rated correct that no golden case was promoted from yet. */
export interface GoldenCandidateItem {
  signalId: number;
  projectId: number;
  dataSourceId: number;
  question: string;
  /** The SQL a promotion starts from; the promotion checks it again and stores it without comments. */
  sql: string;
  isUserCorrected: boolean;
  feedbackNote: string | null;
  askedByUserId: number | null;
  askedByName: string | null;
  ratedByUserId: number | null;
  ratedByName: string | null;
  createdTime: string;
  /** SHA-256 of `sql`; a promotion sends it back as what the Admin reviewed. */
  sqlHash: string;
}

/** `GET /eval/golden` (Admin): a golden case, with who promoted and who last edited it (null: not recorded). */
export interface GoldenCaseItem {
  id: number;
  projectId: number;
  dataSourceId: number;
  question: string;
  goldSql: string;
  sourceSignalId: number | null;
  isActive: boolean;
  notes: string | null;
  createdTime: string;
  promotedByUserId: number | null;
  promotedByName: string | null;
  modifiedByUserId: number | null;
  modifiedByName: string | null;
  /** True when prompts leave the case out: its gold SQL cannot be shown without comments, or holds hidden characters. */
  goldSqlWithheld?: boolean;
  /** True when prompts leave the case out: its question holds characters that do not show. */
  questionWithheld?: boolean;
}

/** Why prompts leave a golden case out, one reason per refused part; empty when they use it. */
export function goldenWithheldReasons(goldenCase: Pick<GoldenCaseItem, 'goldSqlWithheld' | 'questionWithheld'>): string[] {
  const reasons: string[] = [];
  if (goldenCase.goldSqlWithheld) {
    reasons.push('Its gold SQL holds a comment that cannot be removed safely, or characters that do not show on screen.');
  }
  if (goldenCase.questionWithheld) {
    reasons.push('Its question holds characters that do not show on screen.');
  }
  return reasons;
}

/** Why prompts leave a pattern's example out (the lesson itself stays), one reason per refused part. */
export function patternWithheldReasons(
  pattern: Pick<LearnedPatternEntry, 'exampleSqlWithheld' | 'exampleQuestionWithheld'>,
): string[] {
  const reasons: string[] = [];
  if (pattern.exampleSqlWithheld) {
    reasons.push('Its example SQL holds a comment that cannot be removed safely, or characters that do not show on screen.');
  }
  if (pattern.exampleQuestionWithheld) {
    reasons.push('Its example question holds characters that do not show on screen.');
  }
  return reasons;
}

/** What a grounding recheck row is: a golden case or a learned pattern. */
export const RECHECK_KIND_LABEL: Record<number, string> = {
  [GroundingRecheckKind.GoldenCase]: 'Golden case',
  [GroundingRecheckKind.LearnedPattern]: 'Learned pattern',
};

/** `GET /eval/grounding/recheck` (Admin): a golden case or learned pattern today's checks refuse, as stored. */
export interface GroundingRecheckItem {
  kind: number;
  id: number;
  dataSourceId: number;
  question: string | null;
  sql: string | null;
  /** Why the question is refused; null when it passes. */
  questionRefusal: string | null;
  /** Why the SQL is refused; null when it passes. */
  sqlRefusal: string | null;
  /** A golden case's promoting user; null for a pattern, and for a case promoted before Beacon recorded it. */
  promotedByUserId: number | null;
  /** A pattern's status (Approved or Auto-approved); null for a golden case. */
  patternStatus: number | null;
  /** A pattern's content hash, which "back to pending" sends back; null for a golden case. */
  contentHash: string | null;
}

export interface RecheckGroundingResult {
  projectId: number;
  goldenCasesChecked: number;
  patternsChecked: number;
  refused: GroundingRecheckItem[];
}

/** `POST /eval/golden/promote`: the case created — or, when the signal was promoted already, that case. */
export interface PromoteSignalToGoldenResult {
  evalCaseId: number;
  alreadyPromoted: boolean;
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

export interface McpToolsResult {
  toolNames: string[];
}

export interface RunMcpToolResult {
  text: string;
  isError: boolean;
}


export const MCP_SETTINGS_KEY = ['mcp', 'settings'] as const;
export const MCP_DEPLOYMENT_LOCKS_KEY = ['mcp', 'settings', 'locks'] as const;
export const mcpProjectSettingsKey = (projectId: number) =>
  ['mcp', 'project-settings', projectId] as const;
export const MCP_TOOLS_KEY = ['mcp', 'tools'] as const;
export const MCP_LEARNING_STATS_KEY = ['mcp', 'learning-stats'] as const;
export const MCP_PATTERNS_KEY = ['mcp', 'patterns'] as const;
export const MCP_PATCHES_KEY = ['mcp', 'patches'] as const;
export const GOLDEN_CANDIDATES_KEY = ['eval', 'golden', 'candidates'] as const;
export const GOLDEN_CASES_KEY = ['eval', 'golden', 'cases'] as const;
export const GROUNDING_RECHECK_KEY = ['eval', 'grounding', 'recheck'] as const;

export function useMcpSettings() {
  return useQuery({
    queryKey: MCP_SETTINGS_KEY,
    queryFn: async () => unwrap<McpSettingsData>(await beaconApi().getMcpSettings()),
  });
}

/** An active deployment lock (`Beacon:Mcp`) and the setting it pins, both as the server names them. */
export interface McpDeploymentLock {
  /** e.g. `RetainQueryContent`. */
  fieldName: string;
  /** e.g. `ForceNoContentRetention`, or `Audit:RequireContent`. */
  lockName: string;
}

export interface McpDeploymentLocks {
  /** The settings the deployment pins, as server field names (e.g. 'EnforceReadOnly'). */
  lockedFields: string[];
  /** Each active lock and the setting it pins. */
  locks: McpDeploymentLock[];
}

/** The settings the deployment pins (Beacon:Mcp locks) and the locks that pin them. */
export function useMcpDeploymentLocks() {
  return useQuery({
    queryKey: MCP_DEPLOYMENT_LOCKS_KEY,
    queryFn: async (): Promise<McpDeploymentLocks> => {
      const result = unwrap<{ lockedFields: string[]; locks?: McpDeploymentLock[] }>(await beaconApi().getMcpDeploymentLocks());
      return { lockedFields: result.lockedFields, locks: result.locks ?? [] };
    },
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

export function useMcpProjectSettings(projectId: number | undefined) {
  return useQuery({
    queryKey: mcpProjectSettingsKey(projectId ?? 0),
    queryFn: async () =>
      unwrap<GetMcpProjectSettingsResult>(
        await beaconApi().getMcpProjectSettings(projectId as number),
      ),
    enabled: typeof projectId === 'number' && Number.isFinite(projectId),
  });
}

export function useUpdateMcpProjectSettings() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ projectId: number; data: McpProjectSettingsData }, void>({
      qc,
      mutationFn: (vars) =>
        beaconApi().updateMcpProjectSettings(vars.projectId, { data: vars.data } as never),
      // The effective values on every scope can change with one project write; the
      // global row itself does not, so only the project key is invalidated.
      invalidate: (vars) => [mcpProjectSettingsKey(vars.projectId)],
      errorFallback: 'Update project MCP settings failed',
    }),
  );
}

export function useMcpTools() {
  return useQuery({
    queryKey: MCP_TOOLS_KEY,
    queryFn: async () => unwrap<McpToolsResult>(await beaconApi().getMcpTools()),
  });
}

export function useRunMcpTool() {
  return useMutation({
    mutationFn: async (vars: {
      toolName: string;
      projectId: number;
      arguments: Record<string, unknown>;
    }) => unwrap<RunMcpToolResult>(await beaconApi().runMcpTool(vars as never)),
  });
}

export function useLearningStats() {
  return useQuery({
    queryKey: MCP_LEARNING_STATS_KEY,
    queryFn: async () =>
      unwrap<LearningStatsResult>(await beaconApi().getLearningStats(undefined)),
  });
}

/** Learned patterns: server-paged, most confident first; status and type filters in the URL (`patterns*`). */
export function useLearnedPatternsList() {
  return usePagedList<LearnedPatternEntry, { status: string; patternType: string }>({
    queryKey: MCP_PATTERNS_KEY,
    path: '/beacon/api/mcp/learned-patterns',
    filters: { status: '', patternType: '' },
    urlPrefix: 'patterns',
  });
}

/** Documentation patches: server-paged, best supported first (`patches*` in the URL). */
export function useDocumentationPatchesList(enabled = true) {
  return usePagedList<DocumentationPatchEntry>({
    enabled,
    queryKey: MCP_PATCHES_KEY,
    path: '/beacon/api/mcp/documentation-patches',
    urlPrefix: 'patches',
  });
}

/** Reviews a pattern: sends the content hash the reviewer saw (`contentHash`); the server answers 409 when it changed. */
export function useUpdatePatternStatus() {
  const qc = useQueryClient();
  return useMutation({
    ...createSimpleMutation<{ patternId: number; newStatus: number; reviewedContentHash: string }, void>({
      qc,
      mutationFn: (vars) =>
        beaconApi().updatePatternStatus(vars.patternId, {
          newStatus: vars.newStatus,
          reviewedContentHash: vars.reviewedContentHash,
        }),
      invalidate: [MCP_PATTERNS_KEY, MCP_LEARNING_STATS_KEY, GROUNDING_RECHECK_KEY],
      errorFallback: 'Update pattern status failed',
    }),
    onError: (err: unknown) =>
      reportReviewError(qc, err, 'Update pattern status failed', [MCP_PATTERNS_KEY, GROUNDING_RECHECK_KEY]),
  });
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

/** Golden candidates (Admin): server-paged, newest first, optionally one project (`candidates*` in the URL). */
export function useGoldenCandidatesList(enabled: boolean) {
  return usePagedList<GoldenCandidateItem, { projectId: string }>({
    queryKey: GOLDEN_CANDIDATES_KEY,
    path: '/beacon/api/eval/golden/candidates',
    filters: { projectId: '' },
    urlPrefix: 'candidates',
    enabled,
  });
}

/** A project's golden cases (Admin). Only read once a project is chosen. */
export function useGoldenCases(projectId: number | null) {
  return useQuery({
    queryKey: [...GOLDEN_CASES_KEY, projectId],
    queryFn: async () =>
      unwrap<GoldenCaseItem[]>(await beaconApi().getGoldenCases(projectId as number, undefined)),
    enabled: projectId !== null,
  });
}

/**
 * Promotes a candidate (Admin), sending the SQL hash the Admin reviewed (`sqlHash`): 409 when the candidate changed since.
 * Idempotent: a signal already promoted answers with its case (`alreadyPromoted`).
 */
export function usePromoteCandidate() {
  const qc = useQueryClient();
  return useMutation({
    ...createSimpleMutation<{ signalId: number; reviewedSqlHash: string }, PromoteSignalToGoldenResult>({
      qc,
      mutationFn: async ({ signalId, reviewedSqlHash }) =>
        unwrap<PromoteSignalToGoldenResult>(
          await beaconApi().promoteSignalToGolden({ signalId, reviewedSqlHash, notes: null }),
        ),
      invalidate: [GOLDEN_CANDIDATES_KEY, GOLDEN_CASES_KEY],
      errorFallback: 'Promote failed',
    }),
    onError: (err: unknown) => reportReviewError(qc, err, 'Promote failed', [GOLDEN_CANDIDATES_KEY]),
  });
}

/**
 * Deactivates a golden case (Admin): prompts stop using it; it stays listed, and can be edited. The other fields are
 * left as they are.
 */
export function useDeactivateGoldenCase() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().updateGoldenCase(id, { question: null, goldSql: null, isActive: false, notes: null }),
      invalidate: [GOLDEN_CASES_KEY, GROUNDING_RECHECK_KEY],
      successMsg: (id) => `Golden case #${id} deactivated.`,
      errorFallback: 'Deactivate golden case failed',
    }),
  );
}

/**
 * The grounding recheck of one project (Admin): its active golden cases and approved learned patterns checked against
 * today's grounding checks, and the ones refused, with the reasons. Read only once `run` is set (an Admin asks for it);
 * it changes nothing.
 */
export function useGroundingRecheck(projectId: number | null, run: boolean) {
  return useQuery({
    queryKey: [...GROUNDING_RECHECK_KEY, projectId],
    queryFn: async () =>
      unwrap<RecheckGroundingResult>(await beaconApi().recheckGrounding(projectId as number)),
    enabled: projectId !== null && run,
    refetchOnWindowFocus: false,
  });
}

export function describeMcpError(err: unknown, fallback: string): string {
  return describeError(err, fallback);
}
