import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { AiActorActionType, AiActorExecutionPhase, AiActorPlanStatus, AiActorStatus } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const ACTOR_STATUS_LABEL: Record<number, string> = {
  [AiActorStatus.Draft]: 'Draft',
  [AiActorStatus.Active]: 'Active',
  [AiActorStatus.Paused]: 'Paused',
  [AiActorStatus.Failed]: 'Failed',
  [AiActorStatus.Archived]: 'Archived',
};

/** A think cycle's phase; Skipped: the cycle did not run because the actor's owner may no longer run its work. */
export const ACTOR_PHASE_LABEL: Record<number, { label: string; tone: 'neutral' | 'ok' | 'warn' | 'crit' | 'info' }> = {
  [AiActorExecutionPhase.Analyzing]: { label: 'Analyzing', tone: 'info' },
  [AiActorExecutionPhase.Planning]: { label: 'Planning', tone: 'info' },
  [AiActorExecutionPhase.Executing]: { label: 'Executing', tone: 'info' },
  [AiActorExecutionPhase.Notifying]: { label: 'Notifying', tone: 'info' },
  [AiActorExecutionPhase.Completed]: { label: 'Completed', tone: 'ok' },
  [AiActorExecutionPhase.Failed]: { label: 'Failed', tone: 'crit' },
  [AiActorExecutionPhase.Skipped]: { label: 'Skipped', tone: 'warn' },
};

// Local strict interfaces bridged via unwrap<T>() — the generated DTOs are
// intentionally loose (optional everywhere, Date-typed fields that are
// strings on the wire). See src/lib/api.ts.
export interface AiActorListItem {
  actorId: number;
  name: string;
  instructions: string;
  dataSourceId: number;
  dataSourceName: string;
  status: AiActorStatus;
  thinkCount: number;
  lastThinkTime: string | null;
  totalCost: number;
  createdTime: string;
}

export interface AiActorDetails {
  actorId: number;
  name: string;
  instructions: string;
  additionalContext: string | null;
  dataSourceId: number;
  dataSourceName: string;
  status: AiActorStatus;
  maxQueries: number;
  maxSubscriptionsPerQuery: number;
  requiresApproval: boolean;
  totalTokensUsed: number;
  totalCost: number;
  lastThinkTime: string | null;
  thinkCount: number;
  lastError: string | null;
  createdTime: string;
  pendingPlanCount: number;
  /** A creator is recorded: background cycles run as the creator, checked at every run. */
  hasOwner: boolean;
  /** The creator's display name or a neutral "User {id}" label; null when no owner or no matching user. Never an id. */
  ownerDisplayName: string | null;
  recentExecutions: AiActorExecutionInfo[];
}

export interface AiActorExecutionInfo {
  executionId: number;
  phase: AiActorExecutionPhase;
  startedAt: string;
  completedAt: string | null;
  queriesCreated: number;
  subscriptionsCreated: number;
  tokensUsed: number;
  estimatedCost: number;
  decisionSummary: string | null;
  errorMessage: string | null;
}

export interface CreateAiActorPayload {
  name: string;
  instructions: string;
  dataSourceId: number;
  additionalContext: string | null;
  maxQueries: number | null;
  maxSubscriptionsPerQuery: number | null;
  defaultRecipientIds: number[] | null;
  activateImmediately: boolean;
}

/** The AI actors grid: server-paged, newest first; data source and archived filters in the URL. */
export function useAiActorsList() {
  return usePagedList<AiActorListItem, { dataSourceId: string; includeArchived: string }>({
    queryKey: ['ai-actors'],
    path: '/beacon/api/ai-actors',
    filters: { dataSourceId: '', includeArchived: '' },
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

export function useAiActorDetailsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['ai-actor', id ?? 0],
    queryFn: async () =>
      unwrap<AiActorDetails>(await beaconApi().getAiActorDetails(id!, 10)),
    enabled: id !== undefined && id > 0,
  });
}

/** Admin only: makes a Beacon user the actor's creator, who may then change and run it. */
export function useSetAiActorOwner(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (userId) => beaconApi().setAiActorOwner(id, { userId }),
      invalidate: [['ai-actor', id], ['ai-actors']],
      successMsg: 'Owner updated',
      errorFallback: 'Changing the owner failed',
    }),
  );
}

export function useCreateAiActor() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateAiActorPayload, unknown>({
      qc,
      mutationFn: (cmd) => beaconApi().createAiActor(cmd),
      // The dialog lets the user pick any data source — invalidate the whole
      // ['ai-actors'] prefix, not just one data source's list.
      invalidate: [['ai-actors']],
      successMsg: 'AI actor created',
      errorFallback: 'Failed to create AI actor',
    }),
  );
}

type Tone = 'neutral' | 'ok' | 'warn' | 'crit' | 'info';

/** A plan's state; only a pending plan that has not expired can be approved, rejected or sent back for revision. */
export const PLAN_STATUS_LABEL: Record<number, { label: string; tone: Tone }> = {
  [AiActorPlanStatus.PendingApproval]: { label: 'Pending approval', tone: 'warn' },
  [AiActorPlanStatus.Executing]: { label: 'Executing', tone: 'info' },
  [AiActorPlanStatus.Executed]: { label: 'Executed', tone: 'ok' },
  [AiActorPlanStatus.Rejected]: { label: 'Rejected', tone: 'crit' },
  [AiActorPlanStatus.Expired]: { label: 'Expired', tone: 'neutral' },
  [AiActorPlanStatus.RevisionRequested]: { label: 'Revision requested', tone: 'neutral' },
};

export const ACTION_TYPE_LABEL: Record<number, string> = {
  [AiActorActionType.CreateQuery]: 'Create query',
  [AiActorActionType.CreateSubscription]: 'Create subscription',
  [AiActorActionType.RefineQuery]: 'Refine query',
  [AiActorActionType.ArchiveQuery]: 'Archive query',
  [AiActorActionType.ArchiveSubscription]: 'Archive subscription',
  [AiActorActionType.SendNotification]: 'Send notification',
};

export interface PendingPlanSummary {
  planId: number;
  actorId: number;
  actorName: string;
  userInstruction: string | null;
  analysis: string;
  actionCount: number;
  proposedAt: string;
  expiresAt: string;
  version: number;
  tokensUsed: number;
  estimatedCost: number;
}

export interface ProposedAction {
  actionType: AiActorActionType;
  reasoning: string;
  parameters: Record<string, unknown>;
  /** A refinement's SQL as it is now. */
  currentSql: string | null;
  /** A refinement's SQL as the plan would make it. */
  proposedSql: string | null;
  isLocked: boolean;
  targetQueryName: string | null;
  targetQueryId: number | null;
}

export interface AiActorPlan {
  planId: number;
  actorId: number;
  actorName: string;
  status: AiActorPlanStatus;
  userInstruction: string | null;
  analysis: string;
  findings: string[];
  proposedActions: ProposedAction[];
  proposedAt: string;
  expiresAt: string;
  isExpired: boolean;
  reviewedAt: string | null;
  reviewerComment: string | null;
  executedAt: string | null;
  executionId: number | null;
  tokensUsed: number;
  estimatedCost: number;
  model: string | null;
  version: number;
  parentPlanId: number | null;
  /** The server's verdict for the caller: pending, not expired, writable, and the approval rules let them. */
  mayApprove: boolean;
  /** Why the caller cannot approve the pending plan; null when they may, or when it is not pending. */
  approveBlockedReason: string | null;
  /** The actor's creator or an Admin may reject the pending plan or send it back for revision. */
  mayRejectOrRevise: boolean;
}

interface PlanDecisionResult {
  success: boolean;
  errorMessage?: string | null;
}

/** The plans an actor proposed that wait for a decision (not expired), newest first. */
export function usePendingPlansQuery(actorId: number | undefined) {
  return useQuery({
    queryKey: ['ai-actor-plans', 'pending', actorId ?? 0],
    queryFn: async () =>
      unwrap<{ plans: PendingPlanSummary[] }>(await beaconApi().getPendingPlans(actorId!)).plans ?? [],
    enabled: actorId !== undefined && actorId > 0,
  });
}

export function useAiActorPlanQuery(planId: number | undefined) {
  return useQuery({
    queryKey: ['ai-actor-plans', 'plan', planId ?? 0],
    queryFn: async () => unwrap<AiActorPlan>(await beaconApi().getAiActorPlan(planId!)),
    enabled: planId !== undefined && planId > 0,
  });
}

/** How many plans wait for the caller's decision, across every actor they may decide for (the navigation badge). */
export function usePendingPlanCount(enabled: boolean) {
  return useQuery({
    queryKey: ['ai-actor-plans', 'count'],
    queryFn: async () => unwrap<{ count: number }>(await beaconApi().getPendingPlanCount()).count ?? 0,
    enabled,
    refetchInterval: 60_000,
  });
}

// A decision the server answers with success false carries its reason: fail the mutation with it.
function decided<T extends PlanDecisionResult>(result: T, fallback: string): T {
  if (!result.success) {
    throw new Error(result.errorMessage || fallback);
  }
  return result;
}

function planDecision<TVars extends { planId: number }>(
  qc: ReturnType<typeof useQueryClient>,
  actorId: number,
  run: (vars: TVars) => Promise<PlanDecisionResult>,
  successMsg: string,
  errorFallback: string,
) {
  return createSimpleMutation<TVars, PlanDecisionResult>({
    qc,
    mutationFn: async (vars) => decided(await run(vars), errorFallback),
    // Every plan query (the pending lists, the plan views, the badge's count) and the actor (its pending count, its cycles).
    invalidate: [['ai-actor-plans'], ['ai-actor', actorId]],
    successMsg,
    errorFallback,
  });
}

/** Approving runs the plan's actions (each re-checked by the server) as the approver. */
export function useApprovePlan(actorId: number) {
  const qc = useQueryClient();
  return useMutation(
    planDecision<{ planId: number; comment: string | null }>(
      qc,
      actorId,
      async ({ planId, comment }) => unwrap<PlanDecisionResult>(await beaconApi().approveAiActorPlan(planId, { comment })),
      'Plan approved',
      'Approving the plan failed',
    ),
  );
}

export function useRejectPlan(actorId: number) {
  const qc = useQueryClient();
  return useMutation(
    planDecision<{ planId: number; reason: string }>(
      qc,
      actorId,
      async ({ planId, reason }) => unwrap<PlanDecisionResult>(await beaconApi().rejectAiActorPlan(planId, { reason })),
      'Plan rejected',
      'Rejecting the plan failed',
    ),
  );
}

/** Sends the plan back: the actor proposes a new version that takes the feedback into account. */
export function useRequestPlanRevision(actorId: number) {
  const qc = useQueryClient();
  return useMutation(
    planDecision<{ planId: number; feedback: string }>(
      qc,
      actorId,
      async ({ planId, feedback }) => unwrap<PlanDecisionResult>(await beaconApi().requestPlanRevision(planId, { feedback })),
      'Revision requested',
      'Requesting a revision failed',
    ),
  );
}
