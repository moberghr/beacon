import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { Button } from '@/components/beacon';
import { useAuth } from './useAuth';

interface RequireAuthProps {
  children: ReactNode;
}

/**
 * Gate for authenticated routes. Anonymous users get redirected to /login
 * via the SPA router (no full page reload), preserving the originally
 * requested URL in location.state.returnTo so the post-login flow can
 * deep-link the user back to where they were going.
 */
export function RequireAuth({ children }: RequireAuthProps) {
  const { data, isLoading, isError, refetch } = useAuth();
  const location = useLocation();

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
        <div className="flex flex-col items-center gap-3">
          <span className="text-crit text-sm">Failed to load authentication state.</span>
          <Button type="button" onClick={() => refetch()}>Retry</Button>
        </div>
      </div>
    );
  }

  if (!data?.isAuthenticated) {
    const returnTo = `${location.pathname}${location.search}`;
    return <Navigate to="/login" replace state={{ returnTo }} />;
  }

  return <>{children}</>;
}
