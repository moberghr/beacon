import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { ArrowRight } from 'lucide-react';
import { beaconApi } from '@/api/client';
import { AUTH_QUERY_KEY, UNAUTHENTICATED_USER } from '@/lib/queryClient';
import {
  AuthLayout,
  AuthAlert,
  EmphasisWord,
  AuthSpinner,
  authLinkButtonClass,
} from './AuthLayout';

export default function LogoutPage() {
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        await beaconApi().logout();
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Sign-out failed');
      } finally {
        // Reset cached auth state and drop the previous user's data so a re-login (or the
        // "Back to sign in" link) never renders stale, still-"authenticated" cache entries.
        queryClient.setQueryData(AUTH_QUERY_KEY, UNAUTHENTICATED_USER);
        queryClient.clear();
        if (!cancelled) setDone(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [queryClient]);

  return (
    <AuthLayout
      eyebrow="SIGN OUT"
      title={
        done && !error ? (
          <>
            See you <EmphasisWord>soon</EmphasisWord>.
          </>
        ) : done && error ? (
          <>
            Sign-out <EmphasisWord>failed</EmphasisWord>.
          </>
        ) : (
          <>
            Signing <EmphasisWord>out</EmphasisWord>…
          </>
        )
      }
      subtitle={
        !done
          ? 'Clearing your Beacon session.'
          : !error
            ? 'Your session has been cleared. The beacon will keep watching.'
            : 'We hit a snag clearing your cookie. You can still return to sign in.'
      }
    >
      {!done && (
        <div
          className="flex max-w-[440px] items-center justify-center gap-3 rounded-md border border-border bg-surface px-4 py-3.5 shadow-sm"
          aria-live="polite"
        >
          <AuthSpinner brand />
          <span className="mono text-xs text-text-muted">clearing session…</span>
        </div>
      )}

      {done && error && <AuthAlert tone="error">{error}</AuthAlert>}

      {done && (
        <Link to="/login" className={authLinkButtonClass()}>
          Back to sign in
          <ArrowRight size={14} />
        </Link>
      )}
    </AuthLayout>
  );
}
