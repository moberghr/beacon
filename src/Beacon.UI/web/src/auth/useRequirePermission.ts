import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { useAccess, type BeaconPermission } from './useAuth';

/**
 * Route gate for a page one of `permissions` opens (an Admin holds them all). Returns `undefined` while the
 * permissions load, `false` for a user without any of them (who is told so and sent home), `true` otherwise.
 *
 * Callers render a loading shell for `undefined` and `null` for `false`.
 */
export function useRequirePermission(...permissions: BeaconPermission[]): boolean | undefined {
  const access = useAccess();
  const navigate = useNavigate();
  const allowed = access.isLoading ? undefined : access.hasAnyPermission(...permissions);
  const required = permissions.join(' or ');

  useEffect(() => {
    if (allowed === false) {
      toast.error(`The ${required} permission is required for this page.`);
      navigate('/home', { replace: true });
    }
  }, [allowed, required, navigate]);

  return allowed;
}
