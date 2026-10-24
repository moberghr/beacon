import { Navigate, Outlet } from 'react-router-dom';
import { useAccess } from './useAuth';

/**
 * The embed grants page exists only while the deployment serves embeds (`Beacon:Embed:Enabled`, off by default) and
 * only for a SecurityAdmin (or an Admin); otherwise a direct link to it leads home, as the server answers its routes
 * 404 or 403.
 */
export function RequireEmbed() {
  const access = useAccess();
  if (access.isLoading) return null;
  if (!access.opensEmbeds) return <Navigate to="/home" replace />;
  return <Outlet />;
}
