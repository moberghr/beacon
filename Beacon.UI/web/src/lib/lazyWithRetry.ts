import { lazy, type ComponentType } from 'react';

const RELOAD_KEY = 'beacon:lazy-reload-attempted';

function isChunkLoadError(error: unknown): boolean {
  if (!(error instanceof Error)) return false;
  const message = error.message || '';
  const name = error.name || '';
  return (
    name === 'ChunkLoadError' ||
    /Loading chunk [\d]+ failed/.test(message) ||
    /Failed to fetch dynamically imported module/.test(message) ||
    /Importing a module script failed/.test(message) ||
    /error loading dynamically imported module/i.test(message)
  );
}

export function lazyWithRetry<T extends ComponentType<any>>(
  factory: () => Promise<{ default: T }>,
): ReturnType<typeof lazy<T>> {
  return lazy(async () => {
    try {
      const mod = await factory();
      sessionStorage.removeItem(RELOAD_KEY);
      return mod;
    } catch (error) {
      if (isChunkLoadError(error) && !sessionStorage.getItem(RELOAD_KEY)) {
        sessionStorage.setItem(RELOAD_KEY, '1');
        window.location.reload();
        return new Promise<{ default: T }>(() => {});
      }
      throw error;
    }
  });
}
