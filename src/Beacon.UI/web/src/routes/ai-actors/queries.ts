import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { AiActorExecutionPhase, AiActorStatus } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';
import { useBackgroundTaskStatus } from '@/lib/useBackgroundTask';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';

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

/** Archives the actor (soft delete); its creator's or an Admin's. */
export function useArchiveAiActor(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, unknown>({
      qc,
      mutationFn: () => beaconApi().archiveAiActor(id),
      invalidate: [['ai-actor', id], ['ai-actors']],
      successMsg: 'AI actor archived',
      errorFallback: 'Archiving the AI actor failed',
    }),
  );
}

/**
 * Starts a think cycle as a background task (202 with its ticket); a 429 says one is already queued or running. Its
 * progress is followed with useAiActorThinkStatus; the cycle in the request (executeAiActorThinkCycle) stays for API callers.
 */
export function useStartAiActorThink(id: number) {
  return useMutation({
    mutationFn: async () => unwrap<BackgroundTaskTicket>(await beaconApi().startAiActorThink(id)),
  });
}

export function useAiActorThinkStatus(id: number, ticket: BackgroundTaskTicket | null) {
  return useBackgroundTaskStatus(['ai-actor', id, 'think'] as const, ticket, async (taskId) =>
    unwrap<BackgroundTaskStatus>(await beaconApi().getAiActorThinkTask(id, taskId)),
  );
}
