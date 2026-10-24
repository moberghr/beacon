import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { unwrap } from '@/lib/api';
import { usePagedList } from '@/lib/usePagedList';
import { windowOf } from '../security-audit/queries';
import type { GetMcpAuditLogsResult, McpAuditLogItem } from '@/api/generated/beacon-api';

export type { McpAuditLogItem };

export interface McpAuditFilters extends Record<string, string> {
  from: string;
  to: string;
  tool: string;
  projectId: string;
  userId: string;
  callerHash: string;
}

const KEY = ['mcp-audit'] as const;
const PAGE_SIZE = 50;

/** A positive whole number typed into an id filter; anything else filters nothing. */
function idParam(value: string): number | undefined {
  return /^\d+$/.test(value.trim()) && Number(value) > 0 ? Number(value) : undefined;
}

/**
 * The MCP audit trail, newest first, over a window of whole UTC days. Page and filters live in the URL (as every grid's do);
 * the request is held back while the window cannot be read. The server pages from 1 and answers no page count, so the
 * pager is derived from the total.
 */
export function useMcpAuditList(defaults: { from: string; to: string }) {
  const list = usePagedList<McpAuditLogItem, McpAuditFilters>({
    queryKey: KEY,
    path: '/beacon/api/mcp/audit',
    pageSize: PAGE_SIZE,
    filters: { from: defaults.from, to: defaults.to, tool: '', projectId: '', userId: '', callerHash: '' },
    enabled: false,
  });
  const { filters, page } = list;
  const auditWindow = windowOf(filters.from, filters.to);

  const query = useQuery<GetMcpAuditLogsResult>({
    queryKey: [...KEY, 'rows', filters, page],
    queryFn: async () =>
      unwrap<GetMcpAuditLogsResult>(
        await beaconApi().getMcpAuditLogs(
          auditWindow.from!,
          auditWindow.to!,
          idParam(filters.projectId),
          filters.tool.trim() || undefined,
          filters.callerHash.trim() || undefined,
          idParam(filters.userId),
          page + 1,
          PAGE_SIZE,
        ),
      ),
    placeholderData: keepPreviousData,
    enabled: auditWindow.error === undefined,
  });

  const totalCount = query.data?.totalCount ?? 0;

  /** One page of the window as filtered, for the export download (not cached: every read is audited). */
  const fetchExportPage = async (exportPage: number, exportPageSize: number) =>
    unwrap<GetMcpAuditLogsResult>(
      await beaconApi().getMcpAuditLogs(
        auditWindow.from!,
        auditWindow.to!,
        idParam(filters.projectId),
        filters.tool.trim() || undefined,
        filters.callerHash.trim() || undefined,
        idParam(filters.userId),
        exportPage,
        exportPageSize,
      ),
    );

  return {
    filters,
    setFilter: list.setFilter,
    resetFilters: list.resetFilters,
    auditWindow,
    fetchExportPage,
    items: query.data?.items ?? [],
    totalCount,
    isLoading: query.isLoading,
    isError: query.isError,
    error: query.error,
    paging: {
      page,
      pageSize: PAGE_SIZE,
      pageCount: Math.ceil(totalCount / PAGE_SIZE),
      totalCount,
      onPageChange: list.setPage,
    },
    loading: query.isFetching,
  };
}
