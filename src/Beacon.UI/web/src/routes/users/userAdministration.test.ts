import { describe, expect, it } from 'vitest';
import { changeBlockedReason, OWN_ACCOUNT_NOTE, PROTECTED_USER_NOTE } from './userAdministration';
import type { RoleEntry, UserEntry } from './queries';

const ROLES: RoleEntry[] = [
  { id: 1, name: 'Admin', description: null, level: 3, isSystemRole: true, permission: null },
  { id: 3, name: 'Viewer', description: null, level: 1, isSystemRole: true, permission: null },
  { id: 10, name: 'Auditor', description: null, level: 0, isSystemRole: true, permission: 'Auditor' },
];

function target(overrides: Partial<UserEntry> = {}): Pick<UserEntry, 'userName' | 'isSuperAdmin' | 'roles'> {
  return { userName: 'bea', isSuperAdmin: false, roles: [{ id: 3, name: 'Viewer', level: 1 }], ...overrides };
}

const userAdmin = { isAdmin: false, userName: 'ada' };
const admin = { isAdmin: true, userName: 'ada' };

describe('changeBlockedReason', () => {
  it('lets a UserAdmin change a user who holds no protected role', () => {
    expect(changeBlockedReason(target(), userAdmin, ROLES)).toBeNull();
  });

  it('keeps a UserAdmin from changing a super admin, even one with only a Viewer role', () => {
    expect(changeBlockedReason(target({ isSuperAdmin: true }), userAdmin, ROLES)).toBe(PROTECTED_USER_NOTE);
  });

  it('keeps a UserAdmin from changing a holder of the Admin role', () => {
    expect(changeBlockedReason(target({ roles: [{ id: 1, name: 'Admin', level: 3 }] }), userAdmin, ROLES))
      .toBe(PROTECTED_USER_NOTE);
  });

  it('keeps a UserAdmin from changing a holder of a functional role', () => {
    expect(changeBlockedReason(target({ roles: [{ id: 10, name: 'Auditor', level: 0 }] }), userAdmin, ROLES))
      .toBe(PROTECTED_USER_NOTE);
  });

  it('keeps a UserAdmin from changing their own account', () => {
    expect(changeBlockedReason(target({ userName: 'ada' }), userAdmin, ROLES)).toBe(OWN_ACCOUNT_NOTE);
  });

  it('lets an Admin change anyone, themselves included', () => {
    expect(changeBlockedReason(target({ isSuperAdmin: true }), admin, ROLES)).toBeNull();
    expect(changeBlockedReason(target({ roles: [{ id: 1, name: 'Admin', level: 3 }] }), admin, ROLES)).toBeNull();
    expect(changeBlockedReason(target({ userName: 'ada' }), admin, ROLES)).toBeNull();
  });
});
