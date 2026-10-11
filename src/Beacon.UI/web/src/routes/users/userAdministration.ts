import type { RoleEntry, UserEntry } from './queries';

/** The role only an Admin assigns or removes, besides the functional roles. */
export const ADMIN_ROLE_NAME = 'Admin';

/** A role that grants a functional permission (one administrative duty, no project data). */
export function isFunctionalRole(role: Pick<RoleEntry, 'permission'>): boolean {
  return role.permission != null && role.permission !== '';
}

/** Whether the signed-in user may assign or remove the role: an Admin any role, a UserAdmin the others. */
export function mayAssignRole(role: Pick<RoleEntry, 'name' | 'permission'>, isAdmin: boolean): boolean {
  return isAdmin || (role.name !== ADMIN_ROLE_NAME && !isFunctionalRole(role));
}

export const OWN_ACCOUNT_NOTE = 'You cannot change your own account or roles; an Admin can.';

export const PROTECTED_USER_NOTE =
  'Only an Admin can change the account of an Admin, a super admin or a holder of a functional role.';

interface Caller {
  isAdmin: boolean;
  /** The signed-in user's Beacon user name. */
  userName: string | null | undefined;
}

/**
 * Why the signed-in user may not change this user's account or roles, or `null` when they may. An Admin changes
 * anyone; a UserAdmin neither themselves nor an Admin, a super admin or a holder of a functional role. The server
 * decides (403); this only keeps the dialog from offering a change it would refuse.
 */
export function changeBlockedReason(
  user: Pick<UserEntry, 'userName' | 'isSuperAdmin' | 'roles'>,
  caller: Caller,
  roles: RoleEntry[],
): string | null {
  if (caller.isAdmin) {
    return null;
  }

  if (caller.userName != null && caller.userName === user.userName) {
    return OWN_ACCOUNT_NOTE;
  }

  const functionalRoleIds = new Set(roles.filter(isFunctionalRole).map(x => x.id));
  const isProtected =
    user.isSuperAdmin || user.roles.some(x => x.name === ADMIN_ROLE_NAME || functionalRoleIds.has(x.id));

  return isProtected ? PROTECTED_USER_NOTE : null;
}
