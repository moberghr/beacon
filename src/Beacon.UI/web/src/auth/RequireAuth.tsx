import { useEffect, type ReactNode } from 'react';
import { Link, Navigate, useLocation } from 'react-router-dom';
import { Button } from '@/components/beacon';
import { redirectToExternalLogin } from './externalLogin';
import { useAuth, usePermissions } from './useAuth';

interface RequireAuthProps {
  children: ReactNode;
}

/**
 * Gate for authenticated routes. Anonymous users get redirected to /login
 * via the SPA router (no full page reload), preserving the originally
 * requested URL in location.state.returnTo so the post-login flow can
 * deep-link the user back to where they were going.
 *
 * On a host that owns sign-in (`externalLogin`), Beacon's login page is skipped: the browser does a
 * full-page redirect to the host's login page, which brings the user back to this URL afterwards.
 */
export function RequireAuth({ children }: RequireAuthProps) {
  const { data, isLoading, isError, refetch } = useAuth();
  const permissions = usePermissions(data?.isAuthenticated === true);
  const location = useLocation();
  const externalLogin = data && !data.isAuthenticated ? data.externalLogin : null;

  useEffect(() => {
    if (externalLogin) {
      const { pathname, search, hash } = window.location;
      redirectToExternalLogin(externalLogin, `${pathname}${search}${hash}`);
    }
  }, [externalLogin]);

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

  if (externalLogin) {
    return (
      <div className="grid place-items-center h-full">
        <span className="text-text-muted text-sm">Redirecting to sign in…</span>
      </div>
    );
  }

  if (!data?.isAuthenticated) {
    const returnTo = `${location.pathname}${location.search}`;
    return <Navigate to="/login" replace state={{ returnTo }} />;
  }

  // The page is rendered only once the user's permissions are known: a user without a role would otherwise see a
  // page whose every request answers 403.
  if (permissions.isPending) {
    return (
      <div className="grid place-items-center h-full">
        <span className="text-text-muted text-sm">Loading…</span>
      </div>
    );
  }

  if (permissions.isError) {
    return (
      <div className="grid place-items-center h-full">
        <div className="flex flex-col items-center gap-3">
          <span className="text-crit text-sm">Failed to load your permissions.</span>
          <Button type="button" onClick={() => permissions.refetch()}>Retry</Button>
        </div>
      </div>
    );
  }

  // Signed in, but no role assigned yet: every page would answer 403, so explain instead.
  if (permissions.data?.canRead === false) {
    return (
      <div className="grid place-items-center h-full">
        <div className="flex max-w-md flex-col items-center gap-3 text-center">
          <span className="text-sm font-medium text-text">Your account has no access yet.</span>
          <span className="text-sm text-text-muted">
            You are signed in, but no role has been assigned to you. Ask a Beacon administrator to grant you access.
          </span>
          <Button type="button" onClick={() => permissions.refetch()} disabled={permissions.isFetching}>
            Check again
          </Button>
          <Link to="/logout" state={{ fromApp: true }} className="text-sm font-medium text-brand-600">
            Sign out
          </Link>
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
