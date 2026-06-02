import type { QueryClient, QueryKey, UseMutationOptions } from '@tanstack/react-query';
import { toast } from 'sonner';
import { describeError } from './api';

type InvalidateInput<TVars, TData> =
  | QueryKey[]
  | ((vars: TVars, data: TData) => QueryKey[]);

export interface CreateSimpleMutationParams<TVars, TData> {
  qc: QueryClient;
  mutationFn: (vars: TVars) => Promise<TData>;
  invalidate?: InvalidateInput<TVars, TData>;
  successMsg?: string | ((vars: TVars, data: TData) => string);
  errorFallback: string;
}

/**
 * Returns a `useMutation` options bag with the project's standard
 * success/error handling: invalidate the listed query keys, surface a
 * success toast, and route every thrown value through `describeError`
 * for a consistent error toast. Pass to `useMutation(...)` directly.
 */
export function createSimpleMutation<TVars, TData>(
  params: CreateSimpleMutationParams<TVars, TData>,
): UseMutationOptions<TData, unknown, TVars> {
  const { qc, mutationFn, invalidate, successMsg, errorFallback } = params;
  return {
    mutationFn: (vars) => mutationFn(vars),
    onSuccess: (data, vars) => {
      const keys =
        typeof invalidate === 'function'
          ? invalidate(vars, data)
          : invalidate ?? [];
      for (const key of keys) {
        qc.invalidateQueries({ queryKey: key });
      }
      if (successMsg !== undefined) {
        const msg =
          typeof successMsg === 'function' ? successMsg(vars, data) : successMsg;
        toast.success(msg);
      }
    },
    onError: (err) => {
      toast.error(describeError(err, errorFallback));
    },
  };
}
