import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import McpPlaygroundPage from './McpPlaygroundPage';

describe('McpPlaygroundPage feedback', () => {
  it('says feedback is recorded for an Admin to review, not promoted', async () => {
    mswServer.use(
      http.get('*/beacon/api/mcp/tools', () => HttpResponse.json({ toolNames: ['ask', 'feedback'] })),
    );

    renderWithProviders(<McpPlaygroundPage />);
    const tool = await screen.findByDisplayValue('ask');
    expect(screen.queryByText(/recorded for an Admin to review/)).not.toBeInTheDocument();

    fireEvent.change(tool, { target: { value: 'feedback' } });

    expect(await screen.findByText(/Feedback is recorded for an Admin to review/)).toBeInTheDocument();
    expect(screen.getByText(/does not become a golden example by itself/)).toBeInTheDocument();
  });

  it('says when the corrected SQL was not kept, and why, while the verdict is recorded', async () => {
    let sent: unknown = null;
    mswServer.use(
      http.get('*/beacon/api/mcp/tools', () => HttpResponse.json({ toolNames: ['ask', 'feedback'] })),
      http.post('*/beacon/api/mcp/tools/run', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({
          text: 'Feedback recorded for signal 12 (correct). The corrected SQL was not kept: the project does not retain query content, so corrected SQL is not stored.',
          isError: false,
        });
      }),
    );

    renderWithProviders(<McpPlaygroundPage />);
    fireEvent.change(await screen.findByDisplayValue('ask'), { target: { value: 'feedback' } });
    expect(screen.getByText(/the answer says when it was not kept, and why/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    fireEvent.change(screen.getByPlaceholderText('signal_id'), { target: { value: '12' } });
    fireEvent.change(screen.getByPlaceholderText('Corrected SQL (optional)'), { target: { value: 'SELECT 1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText(/The corrected SQL was not kept: the project does not retain query content/)).toBeInTheDocument();
    await waitFor(() =>
      expect(sent).toMatchObject({ toolName: 'feedback', projectId: 1, arguments: { signal_id: 12, verdict: 'correct', corrected_sql: 'SELECT 1' } }),
    );
  });

  it('starts on a tool the host serves when it left ask out, and runs that tool', async () => {
    let sent: unknown = null;
    mswServer.use(
      http.get('*/beacon/api/mcp/tools', () => HttpResponse.json({ toolNames: ['get_context', 'search'] })),
      http.post('*/beacon/api/mcp/tools/run', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json({ text: '# Project: Acme Analytics', isError: false });
      }),
    );

    renderWithProviders(<McpPlaygroundPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Send' }));

    await waitFor(() => expect(sent).toMatchObject({ toolName: 'get_context', projectId: 1 }));
  });

  it('sends max_rows within 1..1000 and cuts an oversized answer', async () => {
    let sent: { arguments?: { max_rows?: number } } | null = null;
    mswServer.use(
      http.get('*/beacon/api/mcp/tools', () => HttpResponse.json({ toolNames: ['query'] })),
      http.post('*/beacon/api/mcp/tools/run', async ({ request }) => {
        sent = (await request.json()) as typeof sent;
        return HttpResponse.json({ text: 'x'.repeat(150_000), isError: false });
      }),
    );

    renderWithProviders(<McpPlaygroundPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Project' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '999999' } });
    fireEvent.change(screen.getByPlaceholderText(/SELECT/i), { target: { value: 'SELECT 1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => expect(sent?.arguments?.max_rows).toBe(1000));
    expect(await screen.findByText(/output cut at 100,000 characters/)).toBeInTheDocument();
  });
});
