import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { describeError, httpErrorInfo, unwrap } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';
import { DashboardPermissionLevel, WidgetType } from '@/lib/enums';

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
  /** The owner's display name or a neutral "User {id}" label; null when the owner is not a Beacon user. */
  createdByUserName: string | null;
  /** The caller's level on this dashboard: Admin (owner or admin grantee) may share it and make it the default. */
  userPermissionLevel: DashboardPermissionLevel | null;
  widgets: DashboardWidget[];
}

/** Sharing a dashboard with everyone, or making it the default, needs the dashboard's Admin level. */
export function canChangeSharing(dashboard: Pick<DashboardDetail, 'userPermissionLevel'>): boolean {
  return dashboard.userPermissionLevel === DashboardPermissionLevel.Admin;
}

export const SHARING_FORBIDDEN_MESSAGE =
  "Only the dashboard's owner or an admin grantee can share it or make it the default.";

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

/** The dashboards grid: server-paged; default dashboard first, then sort order, then newest. */
export function useDashboardsList() {
  return usePagedList<DashboardListItem, { searchKeyword: string }>({
    queryKey: DASHBOARDS_KEY,
    path: '/beacon/api/dashboards',
    filters: { searchKeyword: '' },
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

/**
 * Shares the dashboard with everyone, or makes it its owner's default (or undoes either). The update carries the
 * dashboard's other settings unchanged; the server refuses (403) a caller without the dashboard's Admin level.
 */
export function useSetDashboardSharing(dashboard: DashboardDetail) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (change: { isShared: boolean; isDefault: boolean }) =>
      beaconApi().updateDashboard(dashboard.id, {
        name: dashboard.name,
        description: dashboard.description,
        isShared: change.isShared,
        isDefault: change.isDefault,
        refreshIntervalSeconds: dashboard.refreshIntervalSeconds,
        layoutConfiguration: null,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: dashboardKey(dashboard.id) });
      qc.invalidateQueries({ queryKey: DASHBOARDS_KEY });
      toast.success('Sharing updated');
    },
    onError: (err) => {
      // A 403 carries no reason; say which level the change needs.
      toast.error(httpErrorInfo(err)?.status === 403 ? SHARING_FORBIDDEN_MESSAGE : describeError(err, 'Updating sharing failed'));
    },
  });
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
