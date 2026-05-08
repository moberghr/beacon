/**
 * Phase 3 cutover complete: every nav item is a React route. The Blazor host
 * has been removed; this module remains only as a thin compatibility shim so
 * Sidebar.tsx continues to compile without sweeping changes.
 *
 * `isMigrated` always returns true. `resolveNavHref` always returns `/<slug>`
 * (BrowserRouter basename="/" no longer auto-prefixes anything).
 */
export const MIGRATED_PAGES = [
  'projects',
  'home',
  'about',
  'notifications',
  'control-tower',
  'migration-history',
  'recipients',
  'tasks',
  'approvals',
  'api-keys',
  'users',
  'subscriptions',
  'data-sources',
  'admin-settings',
  'settings',
  'data-catalog',
  'data-quality',
  'mcp-playground',
  'mcp-learning',
  'mcp-settings',
  'dashboards',
  'ai-actors',
  'migration-jobs',
  'queries',
] as const;

export type MigratedPage = (typeof MIGRATED_PAGES)[number];

export function isMigrated(_slug: string): boolean {
  return true;
}

/**
 * Resolve a nav item's slug to a routing path. Always `/<slug>` after cutover.
 */
export function resolveNavHref(slug: string, _blazorPath: string): string {
  return `/${slug}`;
}
