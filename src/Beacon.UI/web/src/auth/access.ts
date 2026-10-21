/**
 * The functional permissions the Admin role's duties are split into, as `GET /beacon/api/auth/permissions` names them.
 * An Admin holds every one; a functional role grants one and no project data (it carries no Viewer or Editor role).
 */
export const BEACON_PERMISSIONS = [
  'Approver',
  'Auditor',
  'SecurityAdmin',
  'UserAdmin',
  'DataSourceAdmin',
  'MigrationOperator',
] as const;

export type BeaconPermission = (typeof BEACON_PERMISSIONS)[number];

/** `GET /beacon/api/auth/permissions`: what the signed-in user may do now. */
export interface CurrentPermissions {
  canRead: boolean;
  canWrite: boolean;
  isAdmin: boolean;
  permissions: string[];
  /** Whether this deployment runs data migrations (`Beacon:Migrations:Enabled`, off by default). */
  migrationsEnabled?: boolean;
  /** Whether Beacon is embedded as an MCP gateway (`AddBeaconMcpGateway`): only part of the REST API is served. */
  gatewayMode?: boolean;
}

/** What the signed-in user may do now, with the checks the pages ask. */
export interface Access {
  /** The session or its permissions are still loading; nothing a permission opens is shown yet. */
  isLoading: boolean;
  canRead: boolean;
  canWrite: boolean;
  isAdmin: boolean;
  /** The functional permissions held (an Admin's lists all six). */
  permissions: string[];
  /** True for an Admin, or a holder of the permission. */
  hasPermission: (permission: BeaconPermission) => boolean;
  hasAnyPermission: (...permissions: BeaconPermission[]) => boolean;
  /** Holds functional permissions but no role that reads project data: the pages those permissions open only. */
  isFunctionalOnly: boolean;
  /** Whether this deployment runs data migrations; while it does not, nothing of the feature is shown. */
  migrationsEnabled: boolean;
  /** The migration pages are open: the deployment runs migrations and the user holds MigrationOperator (or is an Admin). */
  opensMigrations: boolean;
  /** Beacon runs as an MCP gateway: only the projects and MCP pages are served, every other route answers 404. */
  gatewayMode: boolean;
}

export function accessOf(data: Partial<CurrentPermissions> | undefined, isLoading: boolean): Access {
  const isAdmin = data?.isAdmin === true;
  const permissions = data?.permissions ?? [];
  const held = new Set(permissions);
  const hasPermission = (permission: BeaconPermission) => isAdmin || held.has(permission);
  const migrationsEnabled = data?.migrationsEnabled === true;

  return {
    isLoading,
    canRead: data?.canRead === true,
    canWrite: data?.canWrite === true,
    isAdmin,
    permissions,
    hasPermission,
    hasAnyPermission: (...wanted) => wanted.some(hasPermission),
    isFunctionalOnly: data !== undefined && data.canRead === false && !isAdmin && permissions.length > 0,
    migrationsEnabled,
    opensMigrations: migrationsEnabled && hasPermission('MigrationOperator'),
    gatewayMode: data?.gatewayMode === true,
  };
}

export interface PermissionPage {
  path: string;
  label: string;
  permissions: BeaconPermission[];
  /** Only the path itself, not the pages under it (a data source's own page needs read permission). */
  exact?: boolean;
  /** A page of data migration, which exists only while the deployment runs migrations. */
  migrations?: boolean;
}

/** The pages a functional permission opens without a Viewer or Editor role. */
export const PERMISSION_PAGES: PermissionPage[] = [
  { path: '/approvals', label: 'Approvals', permissions: ['Approver'] },
  { path: '/users', label: 'User management', permissions: ['UserAdmin'] },
  { path: '/api-keys', label: 'API keys', permissions: ['Auditor', 'UserAdmin'] },
  { path: '/mcp-settings', label: 'MCP settings', permissions: ['Auditor', 'SecurityAdmin'] },
  { path: '/admin-settings', label: 'Admin settings', permissions: ['Auditor', 'SecurityAdmin'] },
  { path: '/security-audit', label: 'Security audit', permissions: ['Auditor'] },
  { path: '/data-sources', label: 'Data sources', permissions: ['DataSourceAdmin'], exact: true },
  { path: '/migration-jobs', label: 'Data migration', permissions: ['MigrationOperator'], migrations: true },
  { path: '/migration-history', label: 'Migration history', permissions: ['MigrationOperator'], migrations: true },
];

/** Pages about the user themselves, open to everyone signed in. */
const PERSONAL_PAGES = ['/settings', '/about'];

/** The pages the user's functional permissions open, in sidebar order. */
export function pagesOpenedBy(access: Access): PermissionPage[] {
  return PERMISSION_PAGES
    .filter(x => access.migrationsEnabled || x.migrations !== true)
    .filter(x => access.hasAnyPermission(...x.permissions));
}

/** Whether a user with no project data may open the page at `pathname`. */
export function opensWithoutProjectData(access: Access, pathname: string): boolean {
  if (PERSONAL_PAGES.some(x => matches(pathname, x, false))) {
    return true;
  }

  return pagesOpenedBy(access).some(x => matches(pathname, x.path, x.exact === true));
}

function matches(pathname: string, path: string, exact: boolean): boolean {
  const trimmed = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return trimmed === path || (!exact && trimmed.startsWith(`${path}/`));
}
