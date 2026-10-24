import { describe, expect, it } from 'vitest';
import { auditListParams, defaultWindow, windowOf } from './queries';
import { actionsOf, categoryOf, describeAction, SECURITY_AUDIT_CATEGORIES, SECURITY_AUDIT_RESOURCE_TYPES } from './catalogue';

describe('windowOf', () => {
  it('reads whole UTC days, from the start of the first to the end of the last', () => {
    expect(windowOf('2026-10-01', '2026-10-14')).toEqual({
      from: new Date('2026-10-01T00:00:00.000Z'),
      to: new Date('2026-10-14T23:59:59.999Z'),
    });
  });

  it('accepts 93 days and refuses longer, backwards and missing windows', () => {
    expect(windowOf('2026-01-01', '2026-04-03').error).toBeUndefined();
    expect(windowOf('2026-01-01', '2026-04-04').error).toBe('The window must not exceed 93 days.');
    expect(windowOf('2026-10-14', '2026-10-01').error).toBe('The last day must not be before the first.');
    expect(windowOf('', '2026-10-01').error).toBe('Choose the first and the last day of the window.');
  });
});

describe('defaultWindow', () => {
  it('is the last seven UTC days, today included', () => {
    expect(defaultWindow(new Date('2026-10-14T12:00:00Z'))).toEqual({ from: '2026-10-08', to: '2026-10-14' });
  });
});

describe('auditListParams', () => {
  it('sends the window as UTC times and only whole positive ids', () => {
    const params = auditListParams({
      from: '2026-10-01',
      to: '2026-10-14',
      category: 'auth',
      action: '',
      actorUserId: '3.5',
      resourceType: '',
      resourceId: ' 8 ',
      projectId: '2',
    });

    expect(params).toMatchObject({
      from: '2026-10-01T00:00:00.000Z',
      to: '2026-10-14T23:59:59.999Z',
      category: 'auth',
      actorUserId: undefined,
      resourceId: '8',
      projectId: 2,
    });
  });
});

describe('the catalogue', () => {
  it('files every action under the category its name starts with, once', () => {
    const all = SECURITY_AUDIT_CATEGORIES.flatMap(x => x.actions);
    expect(new Set(all).size).toBe(all.length);
    for (const category of SECURITY_AUDIT_CATEGORIES) {
      for (const action of category.actions) {
        expect(categoryOf(action)).toBe(category.code);
      }
    }
    expect(actionsOf('')).toHaveLength(all.length);
    expect(actionsOf('tasks')).toEqual(['tasks.task_claimed', 'tasks.task_reassigned', 'tasks.task_resolved', 'tasks.task_auto_resolved']);
    expect(describeAction('delivery.subscription_updated')).toBe('subscription updated');
  });

  it('offers the saved-query category and the actions recorded since the trail began', () => {
    expect(actionsOf('queries')).toEqual(['queries.lock_changed', 'queries.mcp_tool_changed']);
    expect(actionsOf('')).toEqual(expect.arrayContaining([
      'identity.owned_resources_read',
      'access.dashboard_shared',
      'access.dashboard_share_revoked',
      'data_sources.data_source_updated',
      'data_sources.credentials_rotated',
      'curation.relationship_created',
      'curation.glossary_term_created',
      'curation.glossary_term_updated',
      'curation.glossary_term_deleted',
      'curation.documentation_section_edited',
      'curation.documentation_generation_started',
      'migrations.job_started',
      'migrations.job_run_ended',
      'audit.mcp_audit_read',
      'audit.mcp_audit_verified',
      'audit.mcp_audit_sealing_started',
    ]));
    expect(SECURITY_AUDIT_RESOURCE_TYPES).toEqual(expect.arrayContaining(['dashboard', 'glossary_term', 'documentation_section', 'mcp_audit']));
  });
});
