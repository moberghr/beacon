import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import type { QueryDetail } from '../queries';
import { McpToolCard } from './McpToolCard';

function detail(overrides: Partial<QueryDetail> = {}): QueryDetail {
  return {
    id: 42,
    name: 'Loan book by month',
    description: 'Outstanding principal per month.',
    createdTime: '2026-04-15T10:00:00Z',
    totalExecutions: 0,
    sentNotifications: 0,
    steps: [],
    finalQuery: null,
    finalQueryDataSourceId: null,
    aiActorId: null,
    aiActorName: null,
    isLocked: false,
    mcpToolEnabled: false,
    mcpToolName: null,
    mcpToolDescription: null,
    mcpToolRunnableVersionNumber: 3,
    mcpToolIssue: null,
    subscriptions: [],
    notificationHistory: [],
    avgExecutionTimeMs: 0,
    minExecutionTimeMs: 0,
    maxExecutionTimeMs: 0,
    executionTimeHistory: [],
    isMultiStep: false,
    isCrossDataSource: false,
    isCrossDatabase: false,
    dataSourceNames: [],
    ...overrides,
  };
}

function signInAs(roles: string[]) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({
        userId: 'u1',
        displayName: 'Tester',
        email: null,
        isAuthenticated: true,
        roles,
        realtimeEnabled: false,
      }),
    ),
  );
}

describe('McpToolCard', () => {
  it('lets an admin expose a query with an approved version and sends the PUT', async () => {
    signInAs(['Admin']);
    let body: unknown = null;
    mswServer.use(
      http.put('*/beacon/api/queries/42/mcp-tool', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({
          queryId: 42,
          enabled: true,
          name: 'loan_book_by_month',
          description: 'Monthly loan book.',
          toolName: 'q_loan_book_by_month',
          runnableVersionNumber: 3,
          issue: null,
        });
      }),
      http.get('*/beacon/api/queries/42', () => HttpResponse.json(detail())),
    );

    renderWithProviders(<McpToolCard query={detail()} />);

    expect(screen.getByTestId('mcp-tool-runnable')).toHaveTextContent('v3');
    expect(screen.getByText('Not exposed')).toBeInTheDocument();

    fireEvent.click(await screen.findByRole('checkbox', { name: /Expose as MCP tool/i }));
    fireEvent.change(screen.getByPlaceholderText('loan_book_by_month'), { target: { value: 'loan_book_by_month' } });
    fireEvent.change(screen.getByLabelText(/Description/i), { target: { value: 'Monthly loan book.' } });

    expect(screen.getByText('q_loan_book_by_month')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(body).toEqual({ enabled: true, name: 'loan_book_by_month', description: 'Monthly loan book.' });
    });
  });

  it('shows why a query without an approved version cannot be exposed and blocks enabling', async () => {
    signInAs(['Admin']);

    renderWithProviders(
      <McpToolCard
        query={detail({
          mcpToolRunnableVersionNumber: null,
          mcpToolIssue: 'It has no approved active version.',
        })}
      />,
    );

    expect(screen.getByTestId('mcp-tool-runnable')).toHaveTextContent('It has no approved active version.');

    fireEvent.click(await screen.findByRole('checkbox', { name: /Expose as MCP tool/i }));
    fireEvent.change(screen.getByPlaceholderText('loan_book_by_month'), { target: { value: 'loan_book' } });

    expect(screen.getByText(/Approve a version of this query/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('rejects an invalid tool name before calling the server', async () => {
    signInAs(['Admin']);

    renderWithProviders(<McpToolCard query={detail()} />);

    fireEvent.change(await screen.findByPlaceholderText('loan_book_by_month'), { target: { value: 'Loan-Book' } });

    expect(screen.getByText(/lowercase letters, digits or underscores/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('is read-only for a non-admin', async () => {
    signInAs(['Analyst']);

    renderWithProviders(<McpToolCard query={detail({ mcpToolEnabled: true, mcpToolName: 'loan_book_by_month' })} />);

    expect(await screen.findByText('q_loan_book_by_month')).toBeInTheDocument();
    expect(screen.getByText('Exposed')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });
});
