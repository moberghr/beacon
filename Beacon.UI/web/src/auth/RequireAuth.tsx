import type { ReactNode } from 'react';
import { useAuth } from './useAuth';

interface RequireAuthProps {
  children: ReactNode;
}

/**
 * Gate for authenticated routes. Anonymous users get redirected to /login.
 */
export function RequireAuth({ children }: RequireAuthProps) {
  const { data, isLoading, isError } = useAuth();

  if (isLoading) {
    return (
      <div className="grid place-items-center h-full">
        <span className="text-text-muted text-sm">Loading…</span>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="grid place-items-center h-full">
        <span className="text-crit text-sm">Failed to load authentication state.</span>
      </div>
    );
  }

  if (!data?.isAuthenticated) {
    if (typeof window !== 'undefined') {
      window.location.href = '/login';
    }
    return null;
  }

  return <>{children}</>;
}
