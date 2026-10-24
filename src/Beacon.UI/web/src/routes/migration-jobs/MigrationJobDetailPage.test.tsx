import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import MigrationJobDetailPage from './MigrationJobDetailPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), info: vi.fn() },
}));

const job = {
  id: 7,
  name: 'Copy ledger',
  description: 'copy rows',
  dataSourceId: 10,
  dataSourceName: 'ledger-db',
  destinationDataSourceId: 20,
  destinationDataSourceName: 'warehouse',
  destinationTable: 'ledger',
  mode: 1,
  isEnabled: true,
  schedule: null,
  createdTime: '2026-10-20T08:00:00Z',
};

function task(state: number, overrides: Record<string, unknown> = {}) {
  return {
    taskId: 41,
    jobId: 'job-9',
    state,
    createdTime: '2026-10-24T12:00:00Z',
    startedTime: null,
    finishedTime: null,
    failureCode: null,
    warningCount: null,
    ...overrides,
  };
}

const started = http.post('*/beacon/api/migrations/jobs/7/runs', () =>
  HttpResponse.json({ taskId: 41, jobId: 'job-9', state: 0 }, { status: 202 }),
);

let executionReads = 0;

function renderPage(...handlers: Parameters<typeof mswServer.use>) {
  mswServer.use(
    permissionsHandler({ permissions: ['MigrationOperator'] }),
    http.get('*/beacon/api/migrations/jobs/7', () => HttpResponse.json(job)),
    http.get('*/beacon/api/migrations/executions', () => {
      executionReads += 1;
      return HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 });
    }),
    ...handlers,
  );
  return renderWithProviders(
    <Routes>
      <Route path="/migration-jobs/:id" element={<MigrationJobDetailPage />} />
    </Routes>,
    { initialEntries: ['/migration-jobs/7'] },
  );
}

describe('MigrationJobDetailPage run', () => {
  beforeEach(() => {
    executionReads = 0;
  });

  afterEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.mocked(toast.warning).mockClear();
    vi.mocked(toast.info).mockClear();
  });

  it('starts the run as a background task, shows its progress, and reads the run history again when it finishes', async () => {
    let reads = 0;
    renderPage(
      started,
      http.get('*/beacon/api/migrations/jobs/7/runs/41', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? task(1) : task(2, { warningCount: 0 }));
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /run now/i }));

    expect(await screen.findByRole('button', { name: /running/i })).toBeDisabled();
    const before = executionReads;
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Migration finished; the run history has its counts.'), { timeout: 5000 });
    await waitFor(() => expect(executionReads).toBeGreaterThan(before));
    expect(await screen.findByRole('button', { name: /run now/i })).toBeEnabled();
  });

  it('says how many rows failed in a run that finished', async () => {
    renderPage(started, http.get('*/beacon/api/migrations/jobs/7/runs/41', () => HttpResponse.json(task(2, { warningCount: 3 }))));

    fireEvent.click(await screen.findByRole('button', { name: /run now/i }));

    await waitFor(() => expect(toast.warning).toHaveBeenCalledWith(expect.stringContaining('3 failed rows')));
  });

  it('points to the run history when the run failed', async () => {
    renderPage(
      started,
      http.get('*/beacon/api/migrations/jobs/7/runs/41', () => HttpResponse.json(task(3, { failureCode: 'run_failed' }))),
    );

    fireEvent.click(await screen.findByRole('button', { name: /run now/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The migration run failed; its error is in the run history.'));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('says when another run of the job was in progress', async () => {
    renderPage(
      started,
      http.get('*/beacon/api/migrations/jobs/7/runs/41', () => HttpResponse.json(task(4, { failureCode: 'busy' }))),
    );

    fireEvent.click(await screen.findByRole('button', { name: /run now/i }));

    await waitFor(() => expect(toast.info).toHaveBeenCalledWith(expect.stringContaining('Another run of this job is in progress')));
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("says why the run did not start (409), with the server's reason", async () => {
    renderPage(
      http.post('*/beacon/api/migrations/jobs/7/runs', () =>
        HttpResponse.json(
          { title: 'Conflict', status: 409, detail: 'Not run: another run of this job is starting or in progress.' },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: /run now/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('another run of this job is starting or in progress')));
    expect(await screen.findByRole('button', { name: /run now/i })).toBeEnabled();
  });
});
