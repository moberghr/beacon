import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { useIsAdmin } from './useAuth';

/**
 * Admin route gate. Returns the tri-state admin flag from {@link useIsAdmin}:
 * `undefined` while the auth query loads, `false` for non-admins (after which the
 * user is toasted and redirected home), `true` for admins.
 *
 * Callers render a loading shell for `undefined` and `null` for `false`.
 */
export function useRequireAdmin(): boolean | undefined {
  const isAdmin = useIsAdmin();
  const navigate = useNavigate();

  useEffect(() => {
    if (isAdmin === false) {
      toast.error('Admin role required.');
      navigate('/home', { replace: true });
    }
  }, [isAdmin, navigate]);

  return isAdmin;
}
