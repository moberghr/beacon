import { Navigate, Outlet } from 'react-router-dom';
import { useAccess } from './useAuth';

/** Where the MCP gateway's admin UI lands: the pages of the full Beacon are not served there. */
export const GATEWAY_LANDING_PATH = '/mcp-audit';

/**
 * Beacon embedded as an MCP gateway serves only the projects and MCP pages; a direct link to any other page leads to
 * the gateway landing page, as the server answers every other route 404.
 */
export function RequireFullBeacon() {
  const access = useAccess();
  if (access.isLoading) return null;
  if (access.gatewayMode) return <Navigate to={GATEWAY_LANDING_PATH} replace />;
  return <Outlet />;
}
