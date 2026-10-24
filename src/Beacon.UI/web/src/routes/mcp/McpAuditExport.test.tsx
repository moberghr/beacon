import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import { downloadJson } from '@/lib/auditExport';
import McpAuditPage from './McpAuditPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
}));
vi.mock('@/lib/auditExport', async importOriginal => ({
  ...(await importOriginal<typeof import('@/lib/auditExport')>()),
  downloadJson: vi.fn(),
}));

/** The Auditor's export of the MCP audit window: every page of the filtered window, saved as the server returned it. */

function row(id: number) {
  return {
    id, createdTime: '2026-10-10T08:00:00Z', userId: 3, tool: `tool_${id}`, parameters: null, dataSourceId: 1, projectId: 2,
    executionTimeMs: 1, resultRowCount: 1, errorMessage: null, callerKind: 'user', callerHash: 'h', traceId: null, spanId: null,
    mcpSessionId: null, upstreamRequestId: null, apiKeyId: null, contentHash: null, statements: [],
  };
}

describe('MCP audit export', () => {
  beforeEach(() => {
    vi.mocked(downloadJson).mockClear();
  });

  it('reads the whole window page by page and downloads the rows as returned', async () => {
    const total = 130;
    const pages: string[] = [];
    mswServer.use(
      permissionsHandler({ permissions: ['Auditor'] }),
      http.get('*/beacon/api/mcp/audit', ({ request }) => {
        const url = new URL(request.url);
        const page = Number(url.searchParams.get('page'));
        const size = Number(url.searchParams.get('pageSize'));
        pages.push(`${page}x${size}`);
        const items = Array.from({ length: Math.max(0, Math.min(size, total - (page - 1) * size)) }, (_, i) => row((page - 1) * size + i + 1));
        return HttpResponse.json({ items, totalCount: total, page, pageSize: size });
      }),
    );

    renderWithProviders(<McpAuditPage />);
    const button = await screen.findByRole('button', { name: 'Export JSON' });
    await waitFor(() => expect(button).toBeEnabled());
    pages.length = 0;
    fireEvent.click(button);

    await waitFor(() => expect(downloadJson).toHaveBeenCalledTimes(1));
    const [fileName, rows] = vi.mocked(downloadJson).mock.calls[0];
    expect(fileName).toMatch(/^mcp-audit-\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}\.json$/);
    expect(rows).toHaveLength(130);
    expect(pages).toEqual(['1x100', '2x100']);
  });

  it('is not offered without the Auditor permission', async () => {
    mswServer.use(
      permissionsHandler({ permissions: [], canRead: true }),
      http.get('*/beacon/api/mcp/audit', () => HttpResponse.json({ items: [], totalCount: 0, page: 1, pageSize: 50 })),
    );

    renderWithProviders(<McpAuditPage />);

    await waitFor(() => expect(screen.queryByRole('button', { name: 'Export JSON' })).toBeNull());
  });
});
