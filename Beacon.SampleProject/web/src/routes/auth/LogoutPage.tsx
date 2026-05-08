import { useEffect, useState } from 'react';
import { fetchJson } from '@/lib/api';
import { AUTH_STYLES } from './LoginPage';

export default function LogoutPage() {
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        await fetchJson<unknown>('/beacon/api/auth/logout', { method: 'POST' });
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Sign-out failed');
      } finally {
        if (!cancelled) setDone(true);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  return (
    <div className="auth-shell">
      <div className="auth-card" style={{ textAlign: 'center' }}>
        <h1 className="auth-title">Signing out…</h1>
        {!done && <p className="muted">Clearing your session.</p>}
        {done && !error && (
          <>
            <p className="muted">You have been signed out.</p>
            <a className="btn btn--primary" href="/app/login" style={{ width: '100%', justifyContent: 'center', marginTop: 16 }}>
              Back to sign in
            </a>
          </>
        )}
        {done && error && (
          <>
            <div className="auth-alert auth-alert--error" role="alert">{error}</div>
            <a className="btn" href="/app/login" style={{ width: '100%', justifyContent: 'center' }}>
              Back to sign in
            </a>
          </>
        )}
      </div>
      <style>{AUTH_STYLES}</style>
    </div>
  );
}
