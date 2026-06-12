import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';
import { WidgetType } from '@/lib/enums';

// Local strict mirrors of the loose generated DTOs (see `unwrap` in
// '@/lib/api'). Dates are strings on the wire.

export interface DashboardListItem {
  id: number;
  name: string;
  description: string | null;
  isShared: boolean;
  isDefault: boolean;
  widgetCount: number;
  createdTime: string;
  isOwner: boolean;
  createdByUserName: string | null;
}

export interface DashboardsPageResult {
  data: DashboardListItem[];
  totalCount: number | null;
}

export interface DashboardWidget {
  id: number;
  title: string;
  widgetType: number;
  configurationJson: string;
  positionX: number;
  positionY: number;
  width: number;
  height: number;
  sortOrder: number;
  refreshIntervalSeconds: number | null;
}

export interface DashboardDetail {
  id: number;
  name: string;
  description: string | null;
  isShared: boolean;
  isDefault: boolean;
  refreshIntervalSeconds: number | null;
  createdTime: string;
  widgets: DashboardWidget[];
}

export interface AddWidgetPayload {
  title: string;
  widgetType: number;
  configurationJson: string;
  positionX: number | null;
  positionY: number | null;
  width: number | null;
  height: number | null;
  refreshIntervalSeconds: number | null;
}

export interface CreateDashboardResult {
  dashboardId: number;
}

const DASHBOARDS_KEY = ['dashboards'] as const;
const dashboardKey = (id: number) => ['dashboard', id] as const;

export const DASHBOARDS_PAGE_SIZE = 50;

export function useDashboardsQuery(searchKeyword?: string, page = 0) {
  return useQuery({
    queryKey: [...DASHBOARDS_KEY, searchKeyword ?? '', page],
    queryFn: async () =>
      unwrap<DashboardsPageResult>(
        await beaconApi().getDashboards(
          undefined,
          undefined,
          searchKeyword || undefined,
          page,
          DASHBOARDS_PAGE_SIZE,
        ),
      ),
    placeholderData: keepPreviousData,
  });
}

export function useDashboardQuery(id: number | undefined) {
  return useQuery({
    queryKey: id ? dashboardKey(id) : ['dashboard', 'none'],
    queryFn: async () => unwrap<DashboardDetail>(await beaconApi().getDashboard(id!)),
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
      mutationFn: async (values) =>
        unwrap<CreateDashboardResult>(await beaconApi().createDashboard({
          name: values.name,
          description: values.description ?? null,
          isShared: values.isShared,
          refreshIntervalSeconds: null,
        } as never)),
      invalidate: [DASHBOARDS_KEY],
      errorFallback: 'Failed to create dashboard',
    }),
  );
}

export const WIDGET_TYPE_LABEL: Record<number, string> = {
  [WidgetType.KpiCard]: 'KPI card',
  [WidgetType.LineChart]: 'Line chart',
  [WidgetType.BarChart]: 'Bar chart',
  [WidgetType.PieChart]: 'Pie chart',
  [WidgetType.Table]: 'Table',
  [WidgetType.Gauge]: 'Gauge',
  [WidgetType.Mermaid]: 'Diagram',
};

export function useAddWidget(dashboardId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<AddWidgetPayload, unknown>({
      qc,
      mutationFn: (body) => beaconApi().addWidget(dashboardId, body as never),
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
