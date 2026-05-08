import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

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
  const params = new URLSearchParams({
    page: String(args.page),
    pageSize: String(args.pageSize),
    sortColumn: 'CreatedAt',
    sortDescending: 'true',
  });
  if (resolved !== undefined) params.set('resolved', String(resolved));

  return useQuery({
    queryKey: TASKS_KEY(args),
    queryFn: () => fetchJson<GetTasksResult>(`/beacon/api/tasks?${params.toString()}`),
  });
}

export function useTaskDetailQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'detail', id],
    queryFn: () => fetchJson<TaskDetail>(`/beacon/api/tasks/${id}`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskExecutionsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'executions', id],
    queryFn: () => fetchJson<TaskExecutionsResult>(`/beacon/api/tasks/${id}/executions`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskRelatedQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'related', id],
    queryFn: () => fetchJson<TaskRelatedResult>(`/beacon/api/tasks/${id}/related`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskResultHistoryQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'result-history', id],
    queryFn: () => fetchJson<TaskResultHistoryResult>(`/beacon/api/tasks/${id}/result-history`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useTaskCommentsQuery(id: number | undefined) {
  return useQuery({
    queryKey: ['tasks', 'comments', id],
    queryFn: () => fetchJson<TaskCommentsResult>(`/beacon/api/tasks/${id}/comments`),
    enabled: typeof id === 'number' && Number.isFinite(id),
  });
}

export function useAddTaskComment(id: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (content: string) =>
      fetchJson<{ id: number }>(`/beacon/api/tasks/${id}/comments`, {
        method: 'POST',
        body: JSON.stringify({ content }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['tasks', 'comments', id] });
    },
  });
}

export function useResolveTask() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, resolutionNotes }: { id: number; resolutionNotes: string | null }) =>
      fetchJson<void>(`/beacon/api/tasks/${id}/resolve`, {
        method: 'POST',
        body: JSON.stringify({ resolutionNotes }),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['tasks'] });
    },
  });
}
