import { describe, it, expect, vi, beforeEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { QueryVersionStatus } from '@/lib/enums';
import QueryVersionDetailPage from './QueryVersionDetailPage';
import { restoreMessage } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

const VERSION = {
  id: 5,
  versionNumber: 1,
  label: null,
  status: QueryVersionStatus.Archived,
  name: 'Orders',
  description: null,
  finalQuery: null,
  createdTime: '2026-06-01T10:00:00Z',
  createdByUserName: 'Ana',
  changeSource: 'UserEdit',
  changeReason: null,
  steps: [{ stepOrder: 1, sqlValue: 'SELECT 1', dataSourceId: 4, dataSourceName: 'finance-db', name: 'Step 1', description: null }],
};

function serve(version: Record<string, unknown>, restore?: Record<string, unknown>) {
  mswServer.use(
    http.get('*/beacon/api/query-versions/5', () => HttpResponse.json(version)),
    http.get('*/beacon/api/queries/7', () => HttpResponse.json({ id: 7, name: 'Orders', steps: [], finalQuery: null })),
    http.post('*/beacon/api/query-versions/5/restore', () => HttpResponse.json(restore ?? {})),
  );
}

function renderPage() {
  renderWithProviders(
    <Routes>
      <Route path="/queries/:id/versions/:versionId" element={<QueryVersionDetailPage />} />
      <Route path="/queries/:id/versions" element={<div>Versions page</div>} />
      <Route path="/queries/:id" element={<div>Query page</div>} />
    </Routes>,
    { initialEntries: ['/queries/7/versions/5'] },
  );
}

async function restore() {
  fireEvent.click(await screen.findByRole('button', { name: 'Restore this version' }));
  fireEvent.click(screen.getByRole('button', { name: 'Confirm restore' }));
}

describe('restoring a query version', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
  });

  it('says a restore waits for approval, and shows the history instead of a live query', async () => {
    serve(VERSION, { queryId: 3, submittedForApproval: true, supersededRequests: 1 });

    renderPage();
    await restore();

    expect(await screen.findByText('Versions page')).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith('Restore submitted for approval; it replaces 1 pending request');
  });

  it('says a restore went live when the workflow is off', async () => {
    serve(VERSION, { queryId: 3, submittedForApproval: false, supersededRequests: 0 });

    renderPage();
    await restore();

    expect(await screen.findByText('Query page')).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith('Version restored');
  });

  it.each([
    [QueryVersionStatus.PendingApproval, 'Pending approval'],
    [QueryVersionStatus.Rejected, 'Rejected'],
    [QueryVersionStatus.Superseded, 'Superseded'],
  ])('offers no restore of a version that was never approved (status %s)', async (status, label) => {
    serve({ ...VERSION, status });

    renderPage();

    expect(await screen.findByText(label)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Restore this version' })).not.toBeInTheDocument());
  });

  it('words the toast by how many pending requests a restore replaced', () => {
    expect(restoreMessage({ queryId: 3, submittedForApproval: true, supersededRequests: 0 })).toBe('Restore submitted for approval');
    expect(restoreMessage({ queryId: 3, submittedForApproval: true, supersededRequests: 2 })).toBe(
      'Restore submitted for approval; it replaces 2 pending requests',
    );
    expect(restoreMessage({ queryId: 3, submittedForApproval: false, supersededRequests: 0 })).toBe('Version restored');
  });
});
