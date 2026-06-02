import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';
import type {
  DashboardListData,
  DashboardDetailsData,
  DashboardWidgetData,
  AddWidgetBody,
  CreateDashboardResult,
} from '@/api/generated/beacon-api';

const DASHBOARDS_KEY = ['dashboards'] as const;
const dashboardKey = (id: number) => ['dashboard', id] as const;

export function useDashboardsQuery(searchKeyword?: string) {
  return useQuery({
    queryKey: [...DASHBOARDS_KEY, searchKeyword ?? ''],
    queryFn: async () =>
      beaconApi().getDashboards(undefined, undefined, searchKeyword || undefined, 0, 200),
  });
}

export function useDashboardQuery(id: number | undefined) {
  return useQuery({
    queryKey: id ? dashboardKey(id) : ['dashboard', 'none'],
    queryFn: async () => beaconApi().getDashboard(id!),
    enabled: id !== undefined && id > 0,
  });
}

export function useDeleteDashboard() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (id) => beaconApi().deleteDashboard(id),
      invalidate: [DASHBOARDS_KEY],
      errorFallback: 'Failed to delete dashboard',
    }),
  );
}

export function useCreateDashboard() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ name: string; description?: string | null; isShared: boolean }, CreateDashboardResult>({
      qc,
      mutationFn: (values) =>
        beaconApi().createDashboard({
          name: values.name,
          description: values.description ?? null,
          isShared: values.isShared,
          refreshIntervalSeconds: null,
        } as never),
      invalidate: [DASHBOARDS_KEY],
      errorFallback: 'Failed to create dashboard',
    }),
  );
}

export const WIDGET_TYPE = {
  KpiCard: 1,
  Chart: 2,
  Table: 3,
  Gauge: 4,
  Mermaid: 5,
} as const;

export const WIDGET_TYPE_LABEL: Record<number, string> = {
  1: 'KPI card',
  2: 'Chart',
  3: 'Table',
  4: 'Gauge',
  5: 'Diagram',
};

export function useAddWidget(dashboardId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<AddWidgetBody, unknown>({
      qc,
      mutationFn: (body) => beaconApi().addWidget(dashboardId, body),
      invalidate: [dashboardKey(dashboardId)],
      successMsg: 'Widget added',
      errorFallback: 'Failed to add widget',
    }),
  );
}

export function useDeleteWidget(dashboardId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (widgetId) => beaconApi().deleteWidget(widgetId),
      invalidate: [dashboardKey(dashboardId)],
      successMsg: 'Widget removed',
      errorFallback: 'Failed to remove widget',
    }),
  );
}

export type { DashboardListData, DashboardDetailsData, DashboardWidgetData, AddWidgetBody };
