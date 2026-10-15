import { describe, expect, it } from 'vitest';
import { canClaimTask, canResolveTask, canWorkTask } from './queries';

describe('the task actions the page offers', () => {
  it('lets the assignee and Admins snooze and reprioritise, and nobody else', () => {
    expect(canWorkTask({ assignedToCaller: true }, false)).toBe(true);
    expect(canWorkTask({ assignedToCaller: false }, true)).toBe(true);
    expect(canWorkTask({ assignedToCaller: false }, false)).toBe(false);
    expect(canWorkTask({ assignedToCaller: false }, undefined)).toBe(false);
  });

  it('lets the assignee resolve, an Admin only a task nobody is assigned, and nobody else: claim first', () => {
    expect(canResolveTask({ assignedToCaller: true, hasAssignee: true }, false)).toBe(true);
    expect(canResolveTask({ assignedToCaller: true, hasAssignee: true }, true)).toBe(true);
    expect(canResolveTask({ assignedToCaller: false, hasAssignee: false }, true)).toBe(true);
    expect(canResolveTask({ assignedToCaller: false, hasAssignee: true }, true)).toBe(false);
    expect(canResolveTask({ assignedToCaller: false, hasAssignee: false }, false)).toBe(false);
    expect(canResolveTask({ assignedToCaller: false, hasAssignee: true }, undefined)).toBe(false);
  });

  it('lets anyone claim an unassigned task, and only an Admin take a task assigned to someone else', () => {
    expect(canClaimTask({ assignedToCaller: false, hasAssignee: false }, false)).toBe(true);
    expect(canClaimTask({ assignedToCaller: false, hasAssignee: true }, false)).toBe(false);
    expect(canClaimTask({ assignedToCaller: false, hasAssignee: true }, true)).toBe(true);
    expect(canClaimTask({ assignedToCaller: true, hasAssignee: true }, true)).toBe(false);
  });
});
