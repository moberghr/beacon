/**
 * Pages migrated to React. Sidebar nav uses this list to decide whether a
 * link points at /app/<path> (React) or /beacon/<path> (Blazor).
 *
 * Each entry is the React route path (without /app prefix). To migrate a
 * page: add it here once the React route renders end-to-end. Do not list
 * a page here until it's actually working in /app/*.
 *
 * After Phase 3 cutover, this file goes away.
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

export function isMigrated(slug: string): boolean {
  return (MIGRATED_PAGES as readonly string[]).includes(slug);
}

/**
 * Resolve a nav item's slug to a routing path.
 *
 * Migrated → `/<slug>` (React Router basename="/app" auto-prefixes /app).
 * Not migrated → `/beacon/<blazorPath>` (absolute, used with native <a>).
 */
export function resolveNavHref(slug: string, blazorPath: string): string {
  return isMigrated(slug) ? `/${slug}` : `/beacon/${blazorPath}`;
}
