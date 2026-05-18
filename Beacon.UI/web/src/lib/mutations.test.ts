import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import { ApiError } from './api';
import { createSimpleMutation } from './mutations';

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

const success = toast.success as ReturnType<typeof vi.fn>;
const error = toast.error as ReturnType<typeof vi.fn>;

afterEach(() => {
  success.mockClear();
  error.mockClear();
});

describe('createSimpleMutation', () => {
  it('invalidates listed keys, toasts success, on resolved mutation', async () => {
    const qc = new QueryClient();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');
    const options = createSimpleMutation({
      qc,
      mutationFn: async (id: number) => ({ id, name: 'x' }),
      invalidate: [['tasks'], ['task', 1]],
      successMsg: 'Saved',
      errorFallback: 'Save failed',
    });

    const data = await options.mutationFn!(1, {} as never);
    options.onSuccess!(data, 1, undefined, {} as never);

    expect(invalidateSpy).toHaveBeenCalledTimes(2);
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['tasks'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['task', 1] });
    expect(success).toHaveBeenCalledWith('Saved');
    expect(error).not.toHaveBeenCalled();
  });

  it('accepts a callback for invalidate keys derived from vars', () => {
    const qc = new QueryClient();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');
    const options = createSimpleMutation({
      qc,
      mutationFn: async (id: number) => id,
      invalidate: (vars) => [['task', vars]],
      successMsg: 'ok',
      errorFallback: 'fail',
    });

    options.onSuccess!(7, 7, undefined, {} as never);

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['task', 7] });
  });

  it('routes errors through describeError to toast.error', () => {
    const qc = new QueryClient();
    const options = createSimpleMutation({
      qc,
      mutationFn: async () => undefined as void,
      errorFallback: 'Save failed',
    });

    options.onError!(new ApiError(400, 'name is required'), undefined as never, undefined, {} as never);

    expect(error).toHaveBeenCalledWith('name is required');
  });

  it('falls back when the thrown value is opaque', () => {
    const qc = new QueryClient();
    const options = createSimpleMutation({
      qc,
      mutationFn: async () => undefined as void,
      errorFallback: 'Save failed',
    });

    options.onError!('weird', undefined as never, undefined, {} as never);

    expect(error).toHaveBeenCalledWith('Save failed');
  });
});
