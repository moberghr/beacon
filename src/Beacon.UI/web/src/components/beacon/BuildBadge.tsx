import { cn } from '@/lib/utils';
import { useAuth, usePermissions } from '@/auth/useAuth';

interface BuildBadgeProps {
  className?: string;
}

/**
 * The running build's identity, `<version> · <short commit>`, with the full value on hover. The server reports it to
 * signed-in callers only (`GET /auth/permissions`); nothing is written into the public bundle. Used in the in-app
 * Sidebar header, so every signed-in view shows what is deployed.
 */
export function BuildBadge({ className }: BuildBadgeProps) {
  const auth = useAuth();
  const build = usePermissions(auth.data?.isAuthenticated === true).data?.build;
  if (!build) {
    return null;
  }
  const commit = build.commit ? build.commit.slice(0, 7) : null;
  return (
    <span
      className={cn('text-2xs mono text-text-subtle', className)}
      title={`Build ${build.version}${build.commit ? `+${build.commit}` : ''}`}
    >
      {commit ? `${build.version} · ${commit}` : build.version}
    </span>
  );
}
