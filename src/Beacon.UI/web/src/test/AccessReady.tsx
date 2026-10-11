import { useAccess } from '@/auth/useAuth';

/**
 * Renders a marker once the signed-in user's permissions are known: rendered next to a page, it lets a test wait until
 * the page has had the chance to show what those permissions open before asserting that something is not shown.
 */
export function AccessReady() {
  const access = useAccess();
  return access.isLoading ? null : <span data-testid="access-ready" hidden />;
}
