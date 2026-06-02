import { useEffect, useState } from 'react';
import { ArrowRight } from 'lucide-react';
import { beaconApi } from '@/api/client';
import { AuthLayout, AuthAlert, EmphasisWord } from './AuthLayout';

export default function LogoutPage() {
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        await beaconApi().logout();
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Sign-out failed');
      } finally {
        if (!cancelled) setDone(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

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
          className="login__rail"
          aria-live="polite"
          style={{ display: 'flex', alignItems: 'center', gap: 12, justifyContent: 'center' }}
        >
          <span
            className="login__spinner"
            style={{
              borderTopColor: 'var(--brand-500)',
              borderColor: 'oklch(58% 0.095 175 / 0.2)',
            }}
          />
          <span className="mono" style={{ color: 'var(--text-muted)', fontSize: 12 }}>
            clearing session…
          </span>
        </div>
      )}

      {done && error && <AuthAlert tone="error">{error}</AuthAlert>}

      {done && (
        <a
          href="/login"
          className="login__submit"
          style={{ textDecoration: 'none', marginTop: 16 }}
        >
          Back to sign in
          <ArrowRight size={14} />
        </a>
      )}
    </AuthLayout>
  );
}
