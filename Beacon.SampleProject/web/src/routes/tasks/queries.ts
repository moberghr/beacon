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
