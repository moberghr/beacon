import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import McpAuditPage from './McpAuditPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/** The Auditor's MCP audit page: the tool calls of a window, newest first, paged, with the server's own fields. */

function row(overrides: Record<string, unknown>) {
  return {
    id: 1,
    createdTime: '2026-10-10T08:00:00Z',
    userId: 3,
    tool: 'query',
    parameters: null,
    dataSourceId: 1,
    projectId: 2,
    executionTimeMs: 125,
    resultRowCount: 40,
    errorMessage: null,
    callerKind: 'user',
    callerHash: 'abc123hash',
    traceId: null,
    spanId: null,
    mcpSessionId: null,
    upstreamRequestId: null,
    apiKeyId: null,
    contentHash: null,
    statements: [],
    ...overrides,
  };
}

function serveAudit(items: unknown[], requests: URL[] = []) {
  mswServer.use(
    permissionsHandler({ permissions: ['Auditor'] }),
    http.get('*/beacon/api/mcp/audit', ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json({ items, totalCount: items.length, page: 1, pageSize: 50 });
    }),
  );
  return requests;
}

describe('McpAuditPage', () => {
  it('lists the calls the server returns with caller, project, duration and outcome', async () => {
    const requests = serveAudit([
      row({ id: 1 }),
      row({ id: 2, tool: 'ask', projectId: null, userId: null, callerKind: 'api_key', callerHash: 'zzz999', errorMessage: 'Statement timed out' }),
    ]);

    renderWithProviders(<McpAuditPage />);

    expect(await screen.findByText('query')).toBeInTheDocument();
    expect(screen.getByText('ask')).toBeInTheDocument();
    expect(screen.getByText('abc123hash')).toBeInTheDocument();
    expect(screen.getByText('api_key')).toBeInTheDocument();
    expect(screen.getByText('Success')).toBeInTheDocument();
    expect(screen.getByText('Statement timed out')).toBeInTheDocument();
    expect(screen.getAllByText('125 ms')).toHaveLength(2);
    expect(requests[0].searchParams.get('page')).toBe('1');
    expect(requests[0].searchParams.get('from')).not.toBeNull();
  });

  it('says so when nothing was recorded', async () => {
    serveAudit([]);

    renderWithProviders(<McpAuditPage />);

    expect(await screen.findByText('No tool calls')).toBeInTheDocument();
  });

  it('reads nothing for a user without the Auditor permission', async () => {
    let reads = 0;
    mswServer.use(
      permissionsHandler({ permissions: ['SecurityAdmin'] }),
      http.get('*/beacon/api/mcp/audit', () => {
        reads++;
        return HttpResponse.json({ items: [], totalCount: 0, page: 1, pageSize: 50 });
      }),
    );

    renderWithProviders(<McpAuditPage />);

    await waitFor(() => expect(screen.queryByText('Loading…')).toBeNull());
    expect(reads).toBe(0);
  });
});
