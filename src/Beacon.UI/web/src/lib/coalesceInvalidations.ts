import type { QueryClient, QueryKey } from '@tanstack/react-query';

export interface InvalidationCoalescer {
  invalidate: (keys: ReadonlyArray<QueryKey>) => void;
  dispose: () => void;
}

interface KeyState {
  lastRun: number;
  timer: ReturnType<typeof setTimeout> | undefined;
}

/**
 * Throttles `invalidateQueries` per key: the first call for a key runs at once, further calls inside `windowMs`
 * collapse into one trailing run at the end of the window. A burst of hub events (a migration emitting a status
 * change per step) then refetches each list at most about once per window, and the last event is never lost.
 */
export function createInvalidationCoalescer(qc: QueryClient, windowMs = 1000): InvalidationCoalescer {
  const states = new Map<string, KeyState>();

  const run = (key: QueryKey, state: KeyState) => {
    state.lastRun = Date.now();
    state.timer = undefined;
    void qc.invalidateQueries({ queryKey: key });
  };

  return {
    invalidate: keys => {
      for (const key of keys) {
        const id = JSON.stringify(key);
        let state = states.get(id);
        if (state === undefined) {
          state = { lastRun: Number.NEGATIVE_INFINITY, timer: undefined };
          states.set(id, state);
        }

        if (state.timer !== undefined) {
          continue;
        }

        const wait = state.lastRun + windowMs - Date.now();
        if (wait <= 0) {
          run(key, state);
        } else {
          const current = state;
          current.timer = setTimeout(() => run(key, current), wait);
        }
      }
    },
    dispose: () => {
      for (const state of states.values()) {
        clearTimeout(state.timer);
        state.timer = undefined;
      }
      states.clear();
    },
  };
}
