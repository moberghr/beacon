import { useEffect, useState } from 'react';
import { beaconApi } from '@/api/client';
import { Button } from '@/components/beacon';
import { AuthShell, AuthAlert } from './LoginPage';

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
    return () => { cancelled = true; };
  }, []);

  return (
    <AuthShell>
      <div className="text-center">
        <h1 className="text-xl font-semibold text-text m-0 mb-2">Signing out…</h1>
        {!done && <p className="text-text-muted m-0">Clearing your session.</p>}
        {done && !error && (
          <>
            <p className="text-text-muted m-0">You have been signed out.</p>
            <a href="/login" className="block mt-4">
              <Button variant="primary" className="w-full justify-center">Back to sign in</Button>
            </a>
          </>
        )}
        {done && error && (
          <>
            <AuthAlert tone="error">{error}</AuthAlert>
            <a href="/login" className="block">
              <Button className="w-full justify-center">Back to sign in</Button>
            </a>
          </>
        )}
      </div>
    </AuthShell>
  );
}
