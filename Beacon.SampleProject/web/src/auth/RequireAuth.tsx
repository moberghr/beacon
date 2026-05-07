import type { ReactNode } from 'react';
import { useAuth } from './useAuth';

interface RequireAuthProps {
  children: ReactNode;
}

/**
 * Gate for /app/* routes. Anonymous users are redirected to the existing
 * Blazor login at /beacon (which is still the auth source until cutover).
 */
export function RequireAuth({ children }: RequireAuthProps) {
  const { data, isLoading, isError } = useAuth();

  if (isLoading) {
    return (
      <div style={{ display: 'grid', placeItems: 'center', height: '100%' }}>
        <span className="muted">Loading…</span>
      </div>
    );
  }

  if (isError) {
    return (
      <div style={{ display: 'grid', placeItems: 'center', height: '100%' }}>
        <span style={{ color: 'var(--crit)' }}>Failed to load authentication state.</span>
      </div>
    );
  }

  if (!data?.isAuthenticated) {
    if (typeof window !== 'undefined') {
      window.location.href = '/beacon';
    }
    return null;
  }

  return <>{children}</>;
}
