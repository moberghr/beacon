import { type ReactNode } from 'react';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { BackgroundTaskStatus, BackgroundTaskTicket } from '@/api/generated/beacon-api';
import { BackgroundTaskState } from '@/lib/enums';
import { isBackgroundTaskFinished, useBackgroundTaskStatus } from './useBackgroundTask';

const hub = vi.hoisted(() => ({ handler: null as null | ((event: { jobId: string; state: string; timestamp: string }) => void) }));

vi.mock('@/lib/useHubEvent', () => ({
  useHubEvent: (_event: string, handler: (event: { jobId: string; state: string; timestamp: string }) => void) => {
    hub.handler = handler;
  },
}));

function status(state: BackgroundTaskState, overrides: Partial<BackgroundTaskStatus> = {}): BackgroundTaskStatus {
  return {
    taskId: 31,
    jobId: 'job-1',
    state,
    createdTime: new Date('2026-10-24T12:00:00Z'),
    startedTime: null,
    finishedTime: null,
    failureCode: null,
    warningCount: null,
    ...overrides,
  };
}

function setup(ticket: BackgroundTaskTicket | null, readStatus: (taskId: number) => Promise<BackgroundTaskStatus>) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );

  return renderHook(() => useBackgroundTaskStatus(['test'], ticket, readStatus), { wrapper });
}

describe('useBackgroundTaskStatus', () => {
  beforeEach(() => {
    hub.handler = null;
  });

  it('reads nothing without a ticket', () => {
    const readStatus = vi.fn();

    const { result } = setup(null, readStatus);

    expect(readStatus).not.toHaveBeenCalled();
    expect(result.current.state).toBeNull();
  });

  it("reads the task's state again when its job's JobStatusChanged event arrives", async () => {
    const readStatus = vi
      .fn()
      .mockResolvedValueOnce(status(BackgroundTaskState.Running))
      .mockResolvedValueOnce(status(BackgroundTaskState.Succeeded, { warningCount: 2 }));

    const { result } = setup({ taskId: 31, jobId: 'job-1', state: BackgroundTaskState.Queued }, readStatus);

    expect(result.current.state).toBe(BackgroundTaskState.Queued);
    await waitFor(() => expect(result.current.state).toBe(BackgroundTaskState.Running));
    act(() => hub.handler?.({ jobId: 'job-1', state: 'Succeeded', timestamp: '2026-10-24T12:00:01Z' }));
    await waitFor(() => expect(result.current.state).toBe(BackgroundTaskState.Succeeded));
    expect(result.current.status?.warningCount).toBe(2);
    expect(readStatus).toHaveBeenCalledWith(31);
  });

  it("ignores another job's event", async () => {
    const readStatus = vi.fn().mockResolvedValue(status(BackgroundTaskState.Running));

    const { result } = setup({ taskId: 31, jobId: 'job-1', state: BackgroundTaskState.Queued }, readStatus);

    await waitFor(() => expect(result.current.state).toBe(BackgroundTaskState.Running));
    act(() => hub.handler?.({ jobId: 'job-2', state: 'Succeeded', timestamp: '2026-10-24T12:00:01Z' }));
    expect(readStatus).toHaveBeenCalledTimes(1);
  });

  it('counts only succeeded and failed tasks as finished', () => {
    expect(isBackgroundTaskFinished(BackgroundTaskState.Queued)).toBe(false);
    expect(isBackgroundTaskFinished(BackgroundTaskState.Running)).toBe(false);
    expect(isBackgroundTaskFinished(BackgroundTaskState.Succeeded)).toBe(true);
    expect(isBackgroundTaskFinished(BackgroundTaskState.Failed)).toBe(true);
    expect(isBackgroundTaskFinished(null)).toBe(false);
  });
});
