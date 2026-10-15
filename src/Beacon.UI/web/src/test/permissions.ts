import { http, HttpResponse } from 'msw';
import { BEACON_PERMISSIONS, type BeaconPermission } from '@/auth/access';

export interface PermissionsFixture {
  canRead?: boolean;
  canWrite?: boolean;
  isAdmin?: boolean;
  permissions?: BeaconPermission[];
  /** Whether the deployment runs data migrations; on unless a test turns it off. */
  migrationsEnabled?: boolean;
}

/**
 * `GET /auth/permissions` as the server answers it: reads and writes unless told otherwise, and an Admin holds every
 * functional permission.
 */
export function permissionsHandler(fixture: PermissionsFixture = {}) {
  const isAdmin = fixture.isAdmin ?? false;
  return http.get('*/beacon/api/auth/permissions', () =>
    HttpResponse.json({
      canRead: fixture.canRead ?? true,
      canWrite: fixture.canWrite ?? true,
      isAdmin,
      permissions: isAdmin ? [...BEACON_PERMISSIONS] : fixture.permissions ?? [],
      migrationsEnabled: fixture.migrationsEnabled ?? true,
    }),
  );
}

/** The permissions of a session holding these global roles: the `Admin` role makes it an Admin. */
export function permissionsForRoles(roles: string[]) {
  return permissionsHandler({ isAdmin: roles.some(x => x.toLowerCase() === 'admin') });
}

/** A user who holds only these functional roles: no Viewer or Editor role, so no project data. */
export function functionalOnly(...permissions: BeaconPermission[]) {
  return permissionsHandler({ canRead: false, canWrite: false, permissions });
}
