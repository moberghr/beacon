import { describe, expect, it } from 'vitest';
import { canClaimTask, canWorkTask } from './queries';

describe('the task actions the page offers', () => {
  it('lets the assignee and Admins resolve, snooze and reprioritise, and nobody else', () => {
    expect(canWorkTask({ assignedToCaller: true }, false)).toBe(true);
    expect(canWorkTask({ assignedToCaller: false }, true)).toBe(true);
    expect(canWorkTask({ assignedToCaller: false }, false)).toBe(false);
    expect(canWorkTask({ assignedToCaller: false }, undefined)).toBe(false);
  });

  it('lets anyone claim an unassigned task, and only an Admin take a task assigned to someone else', () => {
    expect(canClaimTask({ assignedToCaller: false, assigneeUserId: null }, false)).toBe(true);
    expect(canClaimTask({ assignedToCaller: false, assigneeUserId: 'ext-ana' }, false)).toBe(false);
    expect(canClaimTask({ assignedToCaller: false, assigneeUserId: 'ext-ana' }, true)).toBe(true);
    expect(canClaimTask({ assignedToCaller: true, assigneeUserId: 'ana' }, true)).toBe(false);
  });
});
