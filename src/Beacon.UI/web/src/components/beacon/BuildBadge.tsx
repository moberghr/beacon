import { cn } from '@/lib/cn';

const COMMIT_SHA = import.meta.env.BEACON_COMMIT_SHA;
const BUILD_DATE = import.meta.env.BEACON_BUILD_DATE;

function formatBuildDate(iso: string): string {
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) {
    return iso;
  }
  // YYYY-MM-DD — terse, locale-agnostic, identifies the day uniquely.
  const y = parsed.getUTCFullYear();
  const m = String(parsed.getUTCMonth() + 1).padStart(2, '0');
  const d = String(parsed.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

interface BuildBadgeProps {
  className?: string;
}

/**
 * Single source of truth for the running build's identity. Renders
 * `<short-sha> · <build-date>` and exposes the full ISO timestamp on hover.
 * Used in the in-app Sidebar header and the login footer so both surfaces
 * agree on what's deployed.
 */
export function BuildBadge({ className }: BuildBadgeProps) {
  // Build-time env vars may be missing (e.g. dev shells without git) —
  // fall back to a "dev" badge instead of rendering "undefined · undefined".
  if (!COMMIT_SHA && !BUILD_DATE) {
    return <span className={cn('text-2xs mono text-text-subtle', className)}>dev</span>;
  }
  const sha = COMMIT_SHA || 'dev';
  const date = BUILD_DATE ? formatBuildDate(BUILD_DATE) : 'unknown';
  return (
    <span
      className={cn('text-2xs mono text-text-subtle', className)}
      title={`Build ${sha} · ${BUILD_DATE || 'unknown build date'}`}
    >
      {sha} · {date}
    </span>
  );
}
