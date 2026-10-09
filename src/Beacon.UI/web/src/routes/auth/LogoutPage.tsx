import { useEffect, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { ArrowRight } from 'lucide-react';
import { beaconApi } from '@/api/client';
import { AUTH_QUERY_KEY, UNAUTHENTICATED_USER } from '@/lib/queryClient';
import {
  AuthLayout,
  AuthAlert,
  EmphasisWord,
  AuthSpinner,
  AuthSubmit,
  authLinkButtonClass,
} from './AuthLayout';

/** Router state the app sets when it sends the user to /logout itself (e.g. the "Sign out" link). */
interface LogoutState {
  fromApp?: boolean;
}

/**
 * Ends the Beacon session. When the app itself navigated here, sign-out starts at once; a visit from anywhere else
 * (a link on another site, a typed URL) asks for a click first, so a link alone can never sign a user out.
 */
export default function LogoutPage() {
  const location = useLocation();
  const fromApp = (location.state as LogoutState | null)?.fromApp === true;
  const [confirmed, setConfirmed] = useState(fromApp);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!confirmed) return;
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
  }, [confirmed, queryClient]);

  if (!confirmed) {
    return (
      <AuthLayout
        eyebrow="SIGN OUT"
        title={
          <>
            Sign <EmphasisWord>out</EmphasisWord>?
          </>
        }
        subtitle="This ends your Beacon session in this browser."
      >
        <AuthSubmit type="button" onClick={() => setConfirmed(true)}>
          Sign out
          <ArrowRight size={14} />
        </AuthSubmit>
        <Link to="/" className={authLinkButtonClass()}>
          Stay signed in
        </Link>
      </AuthLayout>
    );
  }

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
