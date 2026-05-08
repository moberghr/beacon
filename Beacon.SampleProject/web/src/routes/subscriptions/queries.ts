import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 4 — endpoints are not yet in the generated NSwag client
// (codegen requires a running backend). Hand-typed wrappers mirror the C# DTOs
// in `Beacon.Core.Handlers.Subscriptions.*Handler`. Swap to `beaconApi()` once
// `npm run codegen` runs against /openapi/v1.json.

export interface SubscriptionEntry {
  id: number;
  queryId: number;
  queryName: string;
  cronExpression: string;
  recipientCount: number;
  recipientNames: string[];
  aiActorId: number | null;
  aiActorName: string | null;
  createTasks: boolean;
  storeResults: boolean;
}

interface GetSubscriptionsResult {
  entries: SubscriptionEntry[];
}

export interface CreateSubscriptionPayload {
  queryId: number;
  cronExpression: string;
  recipientIds: number[];
  maxRows: number | null;
  timeoutSeconds: number | null;
  includeAttachment: boolean;
  showQuery: boolean;
  storeResults: boolean;
  createTasks: boolean;
}

const SUBSCRIPTIONS_KEY = ['subscriptions'] as const;

export function useSubscriptionsQuery(search?: string) {
  return useQuery({
    queryKey: [...SUBSCRIPTIONS_KEY, search ?? null],
    queryFn: () => {
      const params = new URLSearchParams();
      if (search && search.trim()) {
        params.set('search', search.trim());
      }
      const qs = params.toString();
      return fetchJson<GetSubscriptionsResult>(
        `/beacon/api/subscriptions${qs ? `?${qs}` : ''}`,
      );
    },
  });
}

export function useCreateSubscription() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (values: CreateSubscriptionPayload) =>
      fetchJson<{ success: boolean; message: string | null }>(
        '/beacon/api/subscriptions',
        {
          method: 'POST',
          body: JSON.stringify(values),
        },
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY }),
  });
}

export function useDeleteSubscription() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      fetchJson<void>(`/beacon/api/subscriptions/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY }),
  });
}
