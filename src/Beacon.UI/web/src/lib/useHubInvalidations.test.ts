import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { handleApprovalUpdated, SUPERSEDED_MESSAGE } from './useHubInvalidations';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

describe('handleApprovalUpdated', () => {
  beforeEach(() => {
    vi.mocked(toast.info).mockClear();
  });

  it('tells the requester their pending change was superseded, and refetches approvals and what they govern', () => {
    const qc = new QueryClient();
    const invalidate = vi.spyOn(qc, 'invalidateQueries');

    handleApprovalUpdated(qc, { approvalId: 7, status: 'superseded' });

    expect(toast.info).toHaveBeenCalledWith(SUPERSEDED_MESSAGE);
    expect(invalidate.mock.calls.map(x => x[0]?.queryKey)).toEqual([
      ['approvals'],
      ['query'],
      ['queries'],
      ['migration-jobs'],
      ['recipients'],
      ['data-quality'],
      ['subscriptions'],
    ]);
  });

  it('refetches without a toast for a decision', () => {
    const qc = new QueryClient();

    handleApprovalUpdated(qc, { approvalId: 7, status: 'approved' });

    expect(toast.info).not.toHaveBeenCalled();
  });
});
