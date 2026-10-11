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
}

export function accessOf(data: Partial<CurrentPermissions> | undefined, isLoading: boolean): Access {
  const isAdmin = data?.isAdmin === true;
  const permissions = data?.permissions ?? [];
  const held = new Set(permissions);
  const hasPermission = (permission: BeaconPermission) => isAdmin || held.has(permission);

  return {
    isLoading,
    canRead: data?.canRead === true,
    canWrite: data?.canWrite === true,
    isAdmin,
    permissions,
    hasPermission,
    hasAnyPermission: (...wanted) => wanted.some(hasPermission),
    isFunctionalOnly: data !== undefined && data.canRead === false && !isAdmin && permissions.length > 0,
  };
}

export interface PermissionPage {
  path: string;
  label: string;
  permissions: BeaconPermission[];
  /** Only the path itself, not the pages under it (a data source's own page needs read permission). */
  exact?: boolean;
}

/** The pages a functional permission opens without a Viewer or Editor role. */
export const PERMISSION_PAGES: PermissionPage[] = [
  { path: '/approvals', label: 'Approvals', permissions: ['Approver'] },
  { path: '/users', label: 'User management', permissions: ['UserAdmin'] },
  { path: '/api-keys', label: 'API keys', permissions: ['Auditor', 'UserAdmin'] },
  { path: '/mcp-settings', label: 'MCP settings', permissions: ['Auditor', 'SecurityAdmin'] },
  { path: '/admin-settings', label: 'Admin settings', permissions: ['Auditor', 'SecurityAdmin'] },
  { path: '/data-sources', label: 'Data sources', permissions: ['DataSourceAdmin'], exact: true },
  { path: '/migration-jobs', label: 'Data migration', permissions: ['MigrationOperator'] },
  { path: '/migration-history', label: 'Migration history', permissions: ['MigrationOperator'] },
];

/** Pages about the user themselves, open to everyone signed in. */
const PERSONAL_PAGES = ['/settings', '/about'];

/** The pages the user's functional permissions open, in sidebar order. */
export function pagesOpenedBy(access: Access): PermissionPage[] {
  return PERMISSION_PAGES.filter(x => access.hasAnyPermission(...x.permissions));
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
