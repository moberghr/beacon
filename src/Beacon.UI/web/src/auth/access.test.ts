import { describe, expect, it } from 'vitest';
import { accessOf, BEACON_PERMISSIONS, opensWithoutProjectData, pagesOpenedBy, type BeaconPermission } from './access';

describe('accessOf', () => {
  it('gives an Admin every permission', () => {
    const access = accessOf({ canRead: true, canWrite: true, isAdmin: true, permissions: [] }, false);

    expect(access.hasPermission('UserAdmin')).toBe(true);
    expect(access.hasAnyPermission('MigrationOperator')).toBe(true);
    expect(access.isFunctionalOnly).toBe(false);
  });

  it('gives a holder only the permissions held', () => {
    const access = accessOf({ canRead: true, canWrite: false, isAdmin: false, permissions: ['Auditor'] }, false);

    expect(access.hasPermission('Auditor')).toBe(true);
    expect(access.hasPermission('SecurityAdmin')).toBe(false);
    expect(access.hasAnyPermission('SecurityAdmin', 'Auditor')).toBe(true);
    expect(access.isFunctionalOnly).toBe(false);
  });

  it('marks a user with functional roles and no read permission as functional only', () => {
    expect(accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: ['Approver'] }, false).isFunctionalOnly)
      .toBe(true);
    // No role at all is not functional only: RequireAuth explains that instead.
    expect(accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: [] }, false).isFunctionalOnly)
      .toBe(false);
  });

  it('does not mark an Admin as functional only, whatever the read flag says', () => {
    expect(accessOf({ canRead: false, canWrite: false, isAdmin: true, permissions: ['Auditor'] }, false).isFunctionalOnly)
      .toBe(false);
  });

  it('holds nothing while loading or without data', () => {
    const access = accessOf(undefined, true);

    expect(access.isLoading).toBe(true);
    expect(access.hasPermission('Approver')).toBe(false);
    expect(access.isFunctionalOnly).toBe(false);
  });
});

describe('opensWithoutProjectData', () => {
  const approverAndSources = accessOf(
    { canRead: false, canWrite: false, isAdmin: false, permissions: ['Approver', 'DataSourceAdmin'] },
    false,
  );

  it('opens the pages a permission opens, and the pages under them', () => {
    expect(opensWithoutProjectData(approverAndSources, '/approvals')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/approvals/12')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/approvals-archive')).toBe(false);
  });

  it('opens the data sources page but not a data source, which needs read permission', () => {
    expect(opensWithoutProjectData(approverAndSources, '/data-sources')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/data-sources/')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/data-sources/4')).toBe(false);
  });

  it('opens the personal pages, and no page of a permission not held', () => {
    expect(opensWithoutProjectData(approverAndSources, '/settings')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/about')).toBe(true);
    expect(opensWithoutProjectData(approverAndSources, '/users')).toBe(false);
    expect(opensWithoutProjectData(approverAndSources, '/home')).toBe(false);
  });

  it('lists the pages opened, in order', () => {
    expect(pagesOpenedBy(approverAndSources).map(x => x.path)).toEqual(['/approvals', '/data-sources']);
  });
});

describe('pagesOpenedBy', () => {
  // What each permission's routes admit on the server without a Viewer or Editor role.
  const expected: Record<BeaconPermission, string[]> = {
    Approver: ['/approvals'],
    Auditor: ['/api-keys', '/mcp-settings', '/admin-settings'],
    SecurityAdmin: ['/mcp-settings', '/admin-settings'],
    UserAdmin: ['/users', '/api-keys'],
    DataSourceAdmin: ['/data-sources'],
    MigrationOperator: ['/migration-jobs', '/migration-history'],
  };

  it.each(BEACON_PERMISSIONS.map(x => [x] as const))('opens for a %s exactly the pages its routes admit', permission => {
    const access = accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: [permission] }, false);

    expect(pagesOpenedBy(access).map(x => x.path)).toEqual(expected[permission]);
  });

  it('opens every user\'s API keys to an Auditor and a UserAdmin, and to no other permission', () => {
    for (const permission of BEACON_PERMISSIONS) {
      const access = accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: [permission] }, false);

      expect(opensWithoutProjectData(access, '/api-keys')).toBe(permission === 'Auditor' || permission === 'UserAdmin');
    }
  });

  it('opens a new migration job and a job of its own to a MigrationOperator', () => {
    const access = accessOf({ canRead: false, canWrite: false, isAdmin: false, permissions: ['MigrationOperator'] }, false);

    expect(opensWithoutProjectData(access, '/migration-jobs/new')).toBe(true);
    expect(opensWithoutProjectData(access, '/migration-jobs/3')).toBe(true);
    expect(opensWithoutProjectData(access, '/recipients')).toBe(false);
  });
});
