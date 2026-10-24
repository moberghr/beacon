/**
 * The security audit trail's catalogue as the server records it (`SecurityAuditActions` in Beacon.Core): the categories
 * with their actions, the resource types, the outcomes and the actor kinds. The page's filters offer these; a server test
 * checks that every action the server can record is listed here.
 */
export interface SecurityAuditCategory {
  code: string;
  label: string;
  actions: readonly string[];
}

export const SECURITY_AUDIT_CATEGORIES: readonly SecurityAuditCategory[] = [
  {
    code: 'auth',
    label: 'Authentication',
    actions: [
      'auth.password_sign_in_succeeded',
      'auth.password_sign_in_failed',
      'auth.password_sign_in_refused',
      'auth.break_glass_signed_in',
      'auth.signed_out',
      'auth.signed_out_everywhere',
      'auth.session_ended',
      'auth.sso_signed_in',
      'auth.sso_refused',
      'auth.setup_token_refused',
      'auth.user_locked_out',
    ],
  },
  {
    code: 'identity',
    label: 'Identity',
    actions: [
      'identity.super_admin_created',
      'identity.user_created',
      'identity.user_provisioned',
      'identity.user_updated',
      'identity.user_enabled',
      'identity.user_disabled',
      'identity.user_archived',
      'identity.last_super_admin_change_refused',
      'identity.password_changed',
      'identity.password_reset',
      'identity.roles_changed',
      'identity.owned_resources_read',
    ],
  },
  {
    code: 'keys',
    label: 'API keys',
    actions: ['keys.api_key_created', 'keys.api_key_revoked', 'keys.api_key_revoked_by_administrator'],
  },
  {
    code: 'access',
    label: 'Project access',
    actions: [
      'access.project_created',
      'access.member_added',
      'access.member_role_changed',
      'access.member_removed',
      'access.access_mode_changed',
      'access.data_sources_linked',
      'access.data_source_unlinked',
      'access.repository_token_changed',
      'access.repository_scan_requested',
      'access.repository_scan_ran',
      'access.dashboard_shared',
      'access.dashboard_share_revoked',
    ],
  },
  {
    code: 'data_sources',
    label: 'Data sources',
    actions: [
      'data_sources.data_source_created',
      'data_sources.data_source_updated',
      'data_sources.data_source_deleted',
      'data_sources.metadata_refreshed',
      'data_sources.credentials_rotated',
      'data_sources.migration_writes_changed',
      'data_sources.data_source_disabled',
      'data_sources.data_source_enabled',
    ],
  },
  {
    code: 'delivery',
    label: 'Delivery',
    actions: [
      'delivery.recipient_created',
      'delivery.recipient_updated',
      'delivery.recipient_deleted',
      'delivery.subscription_created',
      'delivery.subscription_updated',
      'delivery.subscription_archived',
      'delivery.subscription_reactivated',
      'delivery.subscription_recipients_added',
      'delivery.subscription_recipient_removed',
      'delivery.subscription_sla_changed',
    ],
  },
  {
    code: 'settings',
    label: 'Settings',
    actions: [
      'settings.mcp_settings_changed',
      'settings.mcp_project_settings_changed',
      'settings.admin_settings_changed',
    ],
  },
  {
    code: 'queries',
    label: 'Saved queries',
    actions: ['queries.lock_changed', 'queries.mcp_tool_changed'],
  },
  {
    code: 'approvals',
    label: 'Approvals',
    actions: [
      'approvals.query_change_submitted',
      'approvals.query_change_approved',
      'approvals.query_change_rejected',
      'approvals.change_submitted',
      'approvals.change_approved',
      'approvals.change_rejected',
      'approvals.ai_plan_submitted',
      'approvals.ai_plan_approved',
      'approvals.ai_plan_rejected',
      'approvals.ai_plan_revision_requested',
      'approvals.ai_actor_created',
      'approvals.ai_actor_think_started',
      'approvals.ai_actor_archived',
    ],
  },
  {
    code: 'ownership',
    label: 'Ownership',
    actions: ['ownership.owner_changed'],
  },
  {
    code: 'tasks',
    label: 'Alert tasks',
    actions: ['tasks.task_claimed', 'tasks.task_reassigned', 'tasks.task_resolved', 'tasks.task_auto_resolved'],
  },
  {
    code: 'curation',
    label: 'Grounding curation',
    actions: [
      'curation.pattern_status_changed',
      'curation.documentation_patch_applied',
      'curation.documentation_patch_rejected',
      'curation.golden_case_promoted',
      'curation.golden_case_edited',
      'curation.relationship_verified',
      'curation.relationship_unverified',
      'curation.relationship_edited',
      'curation.relationship_deleted',
      'curation.grounding_rechecked',
      'curation.relationship_created',
      'curation.glossary_term_created',
      'curation.glossary_term_updated',
      'curation.glossary_term_deleted',
      'curation.documentation_section_edited',
      'curation.documentation_generated',
      'curation.documentation_generation_started',
    ],
  },
  {
    code: 'data_quality',
    label: 'Data quality',
    actions: [
      'data_quality.contract_created',
      'data_quality.contract_updated',
      'data_quality.contract_archived',
      'data_quality.contract_evaluated',
    ],
  },
  {
    code: 'migrations',
    label: 'Data migrations',
    actions: [
      'migrations.job_created',
      'migrations.job_deleted',
      'migrations.job_run_requested',
      'migrations.job_started',
      'migrations.job_run_ended',
    ],
  },
  {
    code: 'embed',
    label: 'Embedding',
    actions: [
      'embed.grant_created',
      'embed.grant_revoked',
      'embed.token_issued',
      'embed.token_refused',
      'embed.query_rendered',
    ],
  },
  {
    code: 'row_security',
    label: 'Row security',
    actions: [
      'row_security.attribute_defined',
      'row_security.attribute_updated',
      'row_security.attribute_archived',
      'row_security.attribute_values_set',
      'row_security.policy_created',
      'row_security.policy_updated',
      'row_security.policy_archived',
      'row_security.table_classified',
    ],
  },
  {
    code: 'audit',
    label: 'The audit itself',
    actions: [
      'audit.read',
      'audit.verified',
      'audit.purged',
      'audit.mcp_audit_read',
      'audit.mcp_audit_verified',
      'audit.mcp_audit_sealing_started',
      'audit.execution_log_read',
      'audit.execution_log_exported',
      'audit.execution_log_verified',
      'audit.execution_log_sealing_started',
      'audit.setting_history_verified',
      'audit.setting_history_sealing_started',
    ],
  },
];

export const SECURITY_AUDIT_RESOURCE_TYPES: readonly string[] = [
  'user',
  'api_key',
  'project',
  'data_source',
  'repository',
  'query',
  'subscription',
  'recipient',
  'data_contract',
  'migration_job',
  'ai_actor',
  'ai_actor_plan',
  'approval_request',
  'change_approval_request',
  'task',
  'settings',
  'learned_pattern',
  'documentation_patch',
  'eval_case',
  'schema_relationship',
  'security_audit',
  'dashboard',
  'glossary_term',
  'documentation_section',
  'mcp_audit',
  'execution_log',
  'attribute_definition',
  'row_policy',
  'table_classification',
  'embed_grant',
];

/** How the actor of an event was authenticated, as the page names it. */
export const SECURITY_AUDIT_ACTOR_KINDS: Readonly<Record<string, string>> = {
  user: 'User',
  session: 'Session',
  api_key: 'API key',
  mcp_caller: 'MCP caller',
  system: 'System',
  embed: 'Embed',
  anonymous: 'Anonymous',
};

/** What the server stores in place of a detail that is not a code (a name, an address, free text). */
export const WITHHELD_VALUE = '[withheld]';

/** The category an action belongs to: the part before its first dot. */
export function categoryOf(action: string): string {
  const dot = action.indexOf('.');
  return dot > 0 ? action.slice(0, dot) : action;
}

/** The actions of `category`, or of every category when none is chosen. */
export function actionsOf(category: string): readonly string[] {
  return category
    ? SECURITY_AUDIT_CATEGORIES.find(x => x.code === category)?.actions ?? []
    : SECURITY_AUDIT_CATEGORIES.flatMap(x => x.actions);
}

/** `delivery.subscription_updated` → `subscription updated`. */
export function describeAction(action: string): string {
  const dot = action.indexOf('.');
  return (dot > 0 ? action.slice(dot + 1) : action).replace(/_/g, ' ');
}
