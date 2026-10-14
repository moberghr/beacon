import { Navigate, Outlet } from 'react-router-dom';
import { useAccess } from './useAuth';

/**
 * The data-migration pages exist only while the deployment runs migrations (`Beacon:Migrations:Enabled`, off by
 * default); otherwise a direct link to one leads home, as the server answers every migration route 404.
 */
export function RequireMigrations() {
  const access = useAccess();
  if (access.isLoading) return null;
  if (!access.migrationsEnabled) return <Navigate to="/home" replace />;
  return <Outlet />;
}
