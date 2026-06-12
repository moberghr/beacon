import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import type { TaskPriority } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export interface TaskEntry {
  id: number;
  subscriptionName: string;
  queryName: string;
  latestResultCount: number;
  notificationCount: number;
  executionCount: number;
  uniqueResultCounts: number;
  createdAt: string;
  resolved: boolean;
  resolvedAt: string | null;
  resolvedByUserName: string | null;
  aiActorId: number | null;
  aiActorName: string | null;
}

export interface GetTasksResult {
  entries: TaskEntry[];
  totalCount: number;
}


export interface TaskDetail {
  id: number;
  queryId: number;
  queryName: string;
  subscriptionId: number;
  subscriptionName: string;
  subscriptionDescription: string | null;
  latestResultCount: number;
  notificationCount: number;
  lastNotificationAt: string | null;
  createdAt: string;
  resolved: boolean;
  resolvedAt: string | null;
  resolvedByUserName: string | null;
  resolutionNotes: string | null;
  aiActorId: number | null;
  aiActorName: string | null;
  lastExecutionAt: string | null;
  cronExpression: string | null;
  priority: TaskPriority;
  assigneeUserId: string | null;
  assigneeUserName: string | null;
  snoozedUntil: string | null;
  slaHours: number | null;
  watcherCount: number;
  isWatching: boolean;
  ownerUserId: string | null;
  ownerUserName: string | null;
}

export interface TaskExecutionItem {
  id: number;
  executedAt: string;
  durationMs: number;
  rowCount: number;
  status: string;
}
export interface TaskExecutionsResult {
  taskId: number;
  executions: TaskExecutionItem[];
}

export interface TaskRelatedItem {
  id: number;
  createdAt: string;
  latestResultCount: number;
  resolved: boolean;
  resolvedAt: string | null;
}
export interface TaskRelatedResult {
  taskId: number;
  related: TaskRelatedItem[];
}

export interface TaskResultHistoryItem {
  sampledAt: string;
  resultCount: number;
}
export interface TaskResultHistoryResult {
  taskId: number;
  points: TaskResultHistoryItem[];
}

export interface TaskCommentItem {
  id: number;
  content: string;
  userName: string | null;
  createdAt: string;
}
export interface TaskCommentsResult {
  taskId: number;
  comments: TaskCommentItem[];
}

export type TaskStatusFilter = 'all' | 'unresolved' | 'resolved';

interface UseTasksArgs {
  status: TaskStatusFilter;
  page: number;
  pageSize: number;
}

const TASKS_KEY = (args: UseTasksArgs) => ['tasks', args] as const;

export function useTasksQuery(args: UseTasksArgs) {
  const resolved = args.status === 'all' ? undefined : args.status === 'resolved';
  return useQuery({
    queryKey: TASKS_KEY(args),
    queryFn: async () =>
      unwrap<GetTasksResult>(await beaconApi().getTasks(
        undefined,
        resolved,
        'CreatedAt',
        true,
        args.page,
        args.pageSize,
      )),
  });
}

export function useTaskDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['task', id],
    queryFn: async () => unwrap<TaskDetail>(await beaconApi().getTaskDetail(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskExecutionsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'executions', id],
    queryFn: async () =>
      unwrap<TaskExecutionsResult>(await beaconApi().getTaskExecutions(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskRelatedQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'related', id],
    queryFn: async () =>
      unwrap<TaskRelatedResult>(await beaconApi().getTaskRelated(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskResultHistoryQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'result-history', id],
    queryFn: async () =>
      unwrap<TaskResultHistoryResult>(await beaconApi().getTaskResultHistory(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskCommentsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'comments', id],
    queryFn: async () =>
      unwrap<TaskCommentsResult>(await beaconApi().getTaskComments(id as number)),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useAddTaskComment(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<string, { id: number }>({
      qc,
      mutationFn: async (content) => {
        const r = await beaconApi().addTaskComment(id, { content });
        return { id: r.id ?? 0 };
      },
      invalidate: [['tasks', 'comments', id]],
      errorFallback: 'Add comment failed',
    }),
  );
}

export function useResolveTask() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; resolutionNotes: string | null }, void>({
      qc,
      mutationFn: ({ id, resolutionNotes }) =>
        beaconApi().resolveTask(id, { resolutionNotes }),
      invalidate: (vars) => [['tasks'], ['task', vars.id]],
      errorFallback: 'Resolve task failed',
    }),
  );
}

// ---------- New mutations (assign / snooze / priority / watch / sla) ---------

const taskInvalidations = (id: number) => [['task', id], ['tasks']] as const;

export function useAssignTask(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ assigneeUserId: string | null }, void>({
      qc,
      mutationFn: (vars) => beaconApi().assignTask(id, vars),
      invalidate: [...taskInvalidations(id)],
      successMsg: 'Assignment updated',
      errorFallback: 'Assign failed',
    }),
  );
}

export function useSnoozeTask(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ snoozeUntil: string | null }, void>({
      qc,
      mutationFn: (vars) => beaconApi().snoozeTask(id, vars as never),
      invalidate: [...taskInvalidations(id)],
      successMsg: 'Snooze updated',
      errorFallback: 'Snooze failed',
    }),
  );
}

export function useSetTaskPriority(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ priority: TaskPriority }, void>({
      qc,
      mutationFn: (vars) => beaconApi().setTaskPriority(id, vars),
      invalidate: [...taskInvalidations(id)],
      successMsg: 'Priority updated',
      errorFallback: 'Priority change failed',
    }),
  );
}

export function useWatchTask(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, void>({
      qc,
      mutationFn: () => beaconApi().watchTask(id),
      invalidate: [...taskInvalidations(id)],
      successMsg: 'Watching task',
      errorFallback: 'Failed to watch task',
    }),
  );
}

export function useUnwatchTask(id: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, void>({
      qc,
      mutationFn: () => beaconApi().unwatchTask(id),
      invalidate: [...taskInvalidations(id)],
      successMsg: 'Stopped watching',
      errorFallback: 'Failed to unwatch task',
    }),
  );
}

export function useSetSubscriptionSla(subscriptionId: number, taskId?: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ slaHours: number | null }, void>({
      qc,
      mutationFn: (vars) => beaconApi().setSubscriptionSla(subscriptionId, vars),
      invalidate:
        typeof taskId === 'number'
          ? [['task', taskId], ['tasks']]
          : [['tasks']],
      successMsg: 'SLA updated',
      errorFallback: 'SLA update failed',
    }),
  );
}
