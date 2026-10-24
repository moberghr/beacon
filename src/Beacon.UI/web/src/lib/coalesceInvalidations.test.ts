import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { createInvalidationCoalescer } from './coalesceInvalidations';

describe('createInvalidationCoalescer', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const keysOf = (spy: { mock: { calls: ReadonlyArray<ReadonlyArray<unknown>> } }) =>
    spy.mock.calls.map(x => (x[0] as { queryKey?: unknown } | undefined)?.queryKey);

  it('runs the first invalidation at once', () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const c = createInvalidationCoalescer(qc, 1000);

    c.invalidate([['a'], ['b']]);

    expect(keysOf(spy)).toEqual([['a'], ['b']]);
  });

  it('collapses a burst for one key into one trailing run at the end of the window', () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const c = createInvalidationCoalescer(qc, 1000);

    c.invalidate([['a']]);
    vi.advanceTimersByTime(100);
    c.invalidate([['a']]);
    c.invalidate([['a']]);
    expect(spy).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(899);
    expect(spy).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1);
    expect(spy).toHaveBeenCalledTimes(2);

    vi.advanceTimersByTime(5000);
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it('throttles keys independently and runs again once the window has passed', () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const c = createInvalidationCoalescer(qc, 1000);

    c.invalidate([['a']]);
    vi.advanceTimersByTime(500);
    c.invalidate([['b']]);
    expect(keysOf(spy)).toEqual([['a'], ['b']]);

    vi.advanceTimersByTime(2000);
    c.invalidate([['a']]);
    expect(keysOf(spy)).toEqual([['a'], ['b'], ['a']]);
  });

  it('drops pending trailing runs on dispose', () => {
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, 'invalidateQueries');
    const c = createInvalidationCoalescer(qc, 1000);

    c.invalidate([['a']]);
    c.invalidate([['a']]);
    c.dispose();
    vi.advanceTimersByTime(5000);

    expect(spy).toHaveBeenCalledTimes(1);
  });
});
