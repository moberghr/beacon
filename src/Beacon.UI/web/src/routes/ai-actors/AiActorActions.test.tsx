import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AiActorStatus, BackgroundTaskState } from '@/lib/enums';
import { AiActorActions } from './AiActorActions';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), info: vi.fn() },
}));

function renderActions(status: AiActorStatus = AiActorStatus.Active) {
  return renderWithProviders(
    <Routes>
      <Route path="/ai-actors" element={<p>Actor list</p>} />
      <Route
        path="/ai-actors/:id"
        element={<AiActorActions actorId={3} actorName="Revenue watcher" status={status} />}
      />
    </Routes>,
    { initialEntries: ['/ai-actors/3'] },
  );
}

function task(state: number, overrides: Record<string, unknown> = {}) {
  return {
    taskId: 31,
    jobId: 'job-1',
    state,
    createdTime: '2026-10-24T12:00:00Z',
    startedTime: null,
    finishedTime: null,
    failureCode: null,
    warningCount: null,
    ...overrides,
  };
}

const started = http.post('*/beacon/api/ai-actors/3/think-tasks', () =>
  HttpResponse.json({ taskId: 31, jobId: 'job-1', state: 0 }, { status: 202 }),
);

afterEach(() => {
  vi.mocked(toast.success).mockClear();
  vi.mocked(toast.error).mockClear();
  vi.mocked(toast.warning).mockClear();
});

describe('AiActorActions think now', () => {
  it('starts a background think cycle, shows its progress, and says when it finished', async () => {
    let reads = 0;
    mswServer.use(
      started,
      http.get('*/beacon/api/ai-actors/3/think-tasks/31', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? task(BackgroundTaskState.Running) : task(BackgroundTaskState.Succeeded));
      }),
    );
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Think now' }));

    expect(await screen.findByRole('button', { name: /thinking|think queued/i })).toBeDisabled();
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Think cycle finished'), { timeout: 5000 });
    expect(await screen.findByRole('button', { name: 'Think now' })).toBeEnabled();
  });

  it('says why a skipped cycle did not run, by its code, as a warning', async () => {
    mswServer.use(
      started,
      http.get('*/beacon/api/ai-actors/3/think-tasks/31', () =>
        HttpResponse.json(task(BackgroundTaskState.Skipped, { failureCode: 'cooldown' })),
      ),
    );
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Think now' }));

    await waitFor(() =>
      expect(toast.warning).toHaveBeenCalledWith('This actor thought recently; think cycles run at most once per cooldown.'),
    );
  });

  it('says a failed cycle failed, without the server detail', async () => {
    mswServer.use(
      started,
      http.get('*/beacon/api/ai-actors/3/think-tasks/31', () =>
        HttpResponse.json(task(BackgroundTaskState.Failed, { failureCode: 'failed' })),
      ),
    );
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Think now' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The think cycle failed; the server log has the details.'));
  });

  it("shows the server's refusal when a cycle is already queued or running (429)", async () => {
    mswServer.use(
      http.post('*/beacon/api/ai-actors/3/think-tasks', () =>
        HttpResponse.json(
          { title: 'Too many requests', status: 429, detail: 'A think cycle of this AI actor is already queued or running.' },
          { status: 429 },
        ),
      ),
    );
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Think now' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(await screen.findByRole('button', { name: 'Think now' })).toBeEnabled();
  });

  it('offers no think now for a paused actor, and no actions at all for an archived one', async () => {
    const { unmount } = renderActions(AiActorStatus.Paused);
    expect(await screen.findByRole('button', { name: 'Archive' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Think now' })).not.toBeInTheDocument();
    unmount();

    renderActions(AiActorStatus.Archived);
    expect(screen.queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Think now' })).not.toBeInTheDocument();
  });
});

describe('AiActorActions archive', () => {
  it('asks first, and archives nothing when cancelled', async () => {
    let archived = false;
    mswServer.use(
      http.delete('*/beacon/api/ai-actors/3', () => {
        archived = true;
        return HttpResponse.json({ success: true, actorId: 3 });
      }),
    );
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Archive' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Revenue watcher/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(archived).toBe(false);
  });

  it('archives on confirm and returns to the actor list', async () => {
    mswServer.use(http.delete('*/beacon/api/ai-actors/3', () => HttpResponse.json({ success: true, actorId: 3 })));
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: 'Archive' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Archive' }));

    expect(await screen.findByText('Actor list')).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith('AI actor archived');
  });
});
