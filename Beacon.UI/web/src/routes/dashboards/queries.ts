import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { beaconApi } from '@/api/client';
import type {
  DashboardListData,
  DashboardDetailsData,
  DashboardWidgetData,
  AddWidgetBody,
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
  return useMutation({
    mutationFn: (id: number) => beaconApi().deleteDashboard(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: DASHBOARDS_KEY }),
  });
}

export function useCreateDashboard() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (values: { name: string; description?: string | null; isShared: boolean }) =>
      beaconApi().createDashboard({
        name: values.name,
        description: values.description ?? null,
        isShared: values.isShared,
        refreshIntervalSeconds: null,
      } as never),
    onSuccess: () => qc.invalidateQueries({ queryKey: DASHBOARDS_KEY }),
  });
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
  return useMutation({
    mutationFn: (body: AddWidgetBody) => beaconApi().addWidget(dashboardId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: dashboardKey(dashboardId) });
      toast.success('Widget added');
    },
    onError: () => {
      toast.error('Failed to add widget');
    },
  });
}

export function useDeleteWidget(dashboardId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (widgetId: number) => beaconApi().deleteWidget(widgetId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: dashboardKey(dashboardId) });
      toast.success('Widget removed');
    },
    onError: () => {
      toast.error('Failed to remove widget');
    },
  });
}

export type { DashboardListData, DashboardDetailsData, DashboardWidgetData, AddWidgetBody };
