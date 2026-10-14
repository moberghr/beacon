import { type ReactNode } from 'react';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { useCreateMigrationJob } from '@/routes/migration-history/queries';
import { noteSubmittedForApproval } from './AwaitingApproval';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const MESSAGE = 'Submitted for approval: the job does not run, manually or on its schedule, until a second person approves it.';

describe('a create the server submitted for approval', () => {
  beforeEach(() => {
    vi.mocked(toast.info).mockClear();
  });

  it("says so in the server's words, and nothing when it was not", () => {
    noteSubmittedForApproval({ awaitingApproval: true, message: MESSAGE });
    noteSubmittedForApproval({ awaitingApproval: false, message: null });
    noteSubmittedForApproval({});

    expect(toast.info).toHaveBeenCalledTimes(1);
    expect(toast.info).toHaveBeenCalledWith(MESSAGE);
  });

  it('keeps what a new migration job answered about its approval', async () => {
    mswServer.use(
      http.post('*/beacon/api/migrations/jobs', () =>
        HttpResponse.json({ migrationJobId: 4, success: true, errorMessage: null, awaitingApproval: true, message: MESSAGE }),
      ),
    );

    const { result } = renderHook(() => useCreateMigrationJob(), { wrapper });
    let created: Awaited<ReturnType<typeof result.current.mutateAsync>> | undefined;
    await act(async () => {
      created = await result.current.mutateAsync({} as never);
    });

    expect(created).toEqual({ migrationJobId: 4, success: true, errorMessage: null, awaitingApproval: true, message: MESSAGE });
  });
});
