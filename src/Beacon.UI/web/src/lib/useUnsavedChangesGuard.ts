import { useCallback, useEffect, useState } from 'react';
import { useHref, useLocation, useNavigate } from 'react-router-dom';

/**
 * Asks before leaving a page that has unsaved edits. The app mounts `<BrowserRouter>`, where React Router's
 * `useBlocker` is unavailable, so this covers what it can: closing or reloading the tab (the browser's own
 * prompt), clicks on in-app links, and the page's own buttons through `leave`. The browser Back button is
 * not caught.
 */
export function useUnsavedChangesGuard(dirty: boolean) {
  const navigate = useNavigate();
  const location = useLocation();
  const basename = useHref('/').replace(/\/$/, '');
  const [pendingPath, setPendingPath] = useState<string | null>(null);

  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirty]);

  useEffect(() => {
    if (!dirty) return;
    const current = location.pathname + location.search + location.hash;
    // Capture phase on document runs before React Router's <Link> handler on the root, so stopping the
    // event here keeps the router from navigating until the user confirms.
    const onClick = (e: MouseEvent) => {
      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
      const anchor = e.target instanceof Element ? e.target.closest('a[href]') : null;
      if (!(anchor instanceof HTMLAnchorElement)) return;
      if ((anchor.target && anchor.target !== '_self') || anchor.hasAttribute('download')) return;
      const url = new URL(anchor.href);
      if (url.origin !== window.location.origin) return;
      // Pages outside the app (e.g. /warp) unload it, which the beforeunload prompt already covers.
      if (basename && url.pathname !== basename && !url.pathname.startsWith(`${basename}/`)) return;
      const target = (url.pathname.slice(basename.length) || '/') + url.search + url.hash;
      if (target === current) return;
      e.preventDefault();
      e.stopPropagation();
      setPendingPath(target);
    };
    document.addEventListener('click', onClick, true);
    return () => document.removeEventListener('click', onClick, true);
  }, [dirty, basename, location.pathname, location.search, location.hash]);

  /** Navigates within the app, asking first while there are unsaved edits. */
  const leave = useCallback(
    (path: string) => {
      if (dirty) {
        setPendingPath(path);
        return;
      }
      navigate(path);
    },
    [dirty, navigate],
  );

  const confirmLeave = useCallback(() => {
    if (pendingPath != null) {
      navigate(pendingPath);
    }
    setPendingPath(null);
  }, [pendingPath, navigate]);

  const cancelLeave = useCallback(() => setPendingPath(null), []);

  return { leaveOpen: pendingPath != null, leave, confirmLeave, cancelLeave };
}
