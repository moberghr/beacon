namespace Beacon.Core.SecurityAudit;

/// <summary>The event families of the security audit trail; an action's category is the part before its dot.</summary>
public static class SecurityAuditCategories
{
    public const string Auth = "auth";
    public const string Identity = "identity";
    public const string Keys = "keys";
    public const string Access = "access";
    public const string DataSources = "data_sources";
    public const string Delivery = "delivery";
    public const string Settings = "settings";
    public const string Approvals = "approvals";
    public const string Queries = "queries";
    public const string Ownership = "ownership";
    public const string Tasks = "tasks";
    public const string Curation = "curation";
    public const string DataQuality = "data_quality";
    public const string Migrations = "migrations";
    public const string RowSecurity = "row_security";
    public const string Embed = "embed";
    public const string Audit = "audit";
}

/// <summary>How an action ended.</summary>
public static class SecurityAuditOutcomes
{
    public const string Success = "success";

    /// <summary>The attempt did not succeed (a wrong password, an unknown subject).</summary>
    public const string Failure = "failure";

    /// <summary>Beacon refused the attempt by rule (local login off, a setup token that does not match).</summary>
    public const string Refused = "refused";

    /// <summary>There was nothing to act on (a verification that found no event to check): neither a success nor a failure.</summary>
    public const string Empty = "empty";
}

/// <summary>The kinds of resource an event names.</summary>
public static class SecurityAuditResources
{
    public const string User = "user";
    public const string ApiKey = "api_key";
    public const string Project = "project";
    public const string DataSource = "data_source";
    public const string Repository = "repository";
    public const string Query = "query";
    public const string Subscription = "subscription";
    public const string Recipient = "recipient";
    public const string DataContract = "data_contract";
    public const string MigrationJob = "migration_job";
    public const string AiActor = "ai_actor";
    public const string AiActorPlan = "ai_actor_plan";
    public const string ApprovalRequest = "approval_request";
    public const string ChangeApprovalRequest = "change_approval_request";
    public const string Task = "task";
    public const string Settings = "settings";
    public const string LearnedPattern = "learned_pattern";
    public const string DocumentationPatch = "documentation_patch";
    public const string EvalCase = "eval_case";
    public const string SchemaRelationship = "schema_relationship";
    public const string SecurityAudit = "security_audit";
    public const string GlossaryTerm = "glossary_term";
    public const string DocumentationSection = "documentation_section";
    public const string McpAudit = "mcp_audit";
    public const string ExecutionLog = "execution_log";
    public const string AttributeDefinition = "attribute_definition";
    public const string RowPolicy = "row_policy";
    public const string TableClassification = "table_classification";
    public const string EmbedGrant = "embed_grant";
}

/// <summary>How the actor of an event was authenticated.</summary>
public static class SecurityAuditActorKinds
{
    /// <summary>A signed-in session of a stored, active Beacon user.</summary>
    public const string User = "user";

    /// <summary>A signed-in session without a stored Beacon user (a host principal): named by a keyed hash.</summary>
    public const string Session = "session";

    public const string ApiKey = "api_key";

    /// <summary>A mapped JWT caller on MCP; named by its user, else by its keyed subject hash.</summary>
    public const string McpCaller = "mcp_caller";

    /// <summary>Work Beacon does for itself (a background job, a startup task).</summary>
    public const string System = "system";

    /// <summary>
    /// An embed grant's secret or an embed token, acting for the grant: named by the grant creator's user, the grant being
    /// the event's resource.
    /// </summary>
    public const string Embed = "embed";

    /// <summary>No authenticated caller (a sign-in attempt, a refused request).</summary>
    public const string Anonymous = "anonymous";
}

/// <summary>
/// The catalogue of security audit actions, named <c>{category}.{what happened}</c> in the past tense. Every action a
/// recorder writes is one of these (<see cref="All"/>); a new event site adds its action here.
/// </summary>
public static class SecurityAuditActions
{
    // Authentication
    public const string PasswordSignInSucceeded = "auth.password_sign_in_succeeded";
    public const string PasswordSignInFailed = "auth.password_sign_in_failed";
    public const string PasswordSignInRefused = "auth.password_sign_in_refused";
    public const string BreakGlassSignedIn = "auth.break_glass_signed_in";
    public const string SignedOut = "auth.signed_out";
    public const string SignedOutEverywhere = "auth.signed_out_everywhere";
    public const string SessionEnded = "auth.session_ended";
    public const string SsoSignedIn = "auth.sso_signed_in";
    public const string SsoRefused = "auth.sso_refused";
    public const string SetupTokenRefused = "auth.setup_token_refused";
    public const string UserLockedOut = "auth.user_locked_out";

    // Identity
    public const string SuperAdminCreated = "identity.super_admin_created";
    public const string UserCreated = "identity.user_created";
    public const string UserProvisioned = "identity.user_provisioned";
    public const string UserUpdated = "identity.user_updated";
    public const string UserEnabled = "identity.user_enabled";
    public const string UserDisabled = "identity.user_disabled";
    public const string UserArchived = "identity.user_archived";
    public const string LastSuperAdminChangeRefused = "identity.last_super_admin_change_refused";
    public const string PasswordChanged = "identity.password_changed";
    public const string PasswordReset = "identity.password_reset";
    public const string RolesChanged = "identity.roles_changed";
    public const string OwnedResourcesRead = "identity.owned_resources_read";

    // API keys
    public const string ApiKeyCreated = "keys.api_key_created";
    public const string ApiKeyRevoked = "keys.api_key_revoked";
    public const string ApiKeyRevokedByAdministrator = "keys.api_key_revoked_by_administrator";

    // Project access
    public const string ProjectCreated = "access.project_created";
    public const string MemberAdded = "access.member_added";
    public const string MemberRoleChanged = "access.member_role_changed";
    public const string MemberRemoved = "access.member_removed";
    public const string AccessModeChanged = "access.access_mode_changed";
    public const string DataSourcesLinked = "access.data_sources_linked";
    public const string DataSourceUnlinked = "access.data_source_unlinked";
    public const string RepositoryTokenChanged = "access.repository_token_changed";
    public const string RepositoryScanRequested = "access.repository_scan_requested";
    public const string RepositoryScanRan = "access.repository_scan_ran";

    // Data sources
    public const string DataSourceCreated = "data_sources.data_source_created";
    public const string DataSourceUpdated = "data_sources.data_source_updated";
    public const string DataSourceDeleted = "data_sources.data_source_deleted";
    public const string MetadataRefreshed = "data_sources.metadata_refreshed";
    public const string CredentialsRotated = "data_sources.credentials_rotated";
    public const string MigrationWritesChanged = "data_sources.migration_writes_changed";
    public const string DataSourceDisabled = "data_sources.data_source_disabled";
    public const string DataSourceEnabled = "data_sources.data_source_enabled";

    // Delivery
    public const string RecipientCreated = "delivery.recipient_created";
    public const string RecipientUpdated = "delivery.recipient_updated";
    public const string RecipientDeleted = "delivery.recipient_deleted";
    public const string SubscriptionCreated = "delivery.subscription_created";
    public const string SubscriptionArchived = "delivery.subscription_archived";
    public const string SubscriptionReactivated = "delivery.subscription_reactivated";
    public const string SubscriptionRecipientsAdded = "delivery.subscription_recipients_added";
    public const string SubscriptionRecipientRemoved = "delivery.subscription_recipient_removed";
    public const string SubscriptionSlaChanged = "delivery.subscription_sla_changed";
    public const string SubscriptionUpdated = "delivery.subscription_updated";

    // Settings
    public const string McpSettingsChanged = "settings.mcp_settings_changed";
    public const string McpProjectSettingsChanged = "settings.mcp_project_settings_changed";
    public const string AdminSettingsChanged = "settings.admin_settings_changed";

    // Saved queries: who may change one, and its exposure to MCP callers as a tool
    public const string QueryLockChanged = "queries.lock_changed";
    public const string QueryMcpToolChanged = "queries.mcp_tool_changed";

    // Approvals: a change submitted for a second person's review, and the decision on it
    public const string QueryChangeSubmitted = "approvals.query_change_submitted";
    public const string QueryChangeApproved = "approvals.query_change_approved";
    public const string QueryChangeRejected = "approvals.query_change_rejected";
    public const string ChangeSubmitted = "approvals.change_submitted";
    public const string ChangeApproved = "approvals.change_approved";
    public const string ChangeRejected = "approvals.change_rejected";
    public const string AiPlanSubmitted = "approvals.ai_plan_submitted";
    public const string AiPlanApproved = "approvals.ai_plan_approved";
    public const string AiPlanRejected = "approvals.ai_plan_rejected";
    public const string AiPlanRevisionRequested = "approvals.ai_plan_revision_requested";
    public const string AiActorCreated = "approvals.ai_actor_created";
    public const string AiActorThinkStarted = "approvals.ai_actor_think_started";
    public const string AiActorArchived = "approvals.ai_actor_archived";

    // Ownership
    public const string OwnerChanged = "ownership.owner_changed";

    // Alert tasks
    public const string TaskClaimed = "tasks.task_claimed";
    public const string TaskReassigned = "tasks.task_reassigned";
    public const string TaskResolved = "tasks.task_resolved";
    public const string TaskAutoResolved = "tasks.task_auto_resolved";

    // Grounding curation
    public const string PatternStatusChanged = "curation.pattern_status_changed";
    public const string DocumentationPatchApplied = "curation.documentation_patch_applied";
    public const string DocumentationPatchRejected = "curation.documentation_patch_rejected";
    public const string GoldenCasePromoted = "curation.golden_case_promoted";
    public const string GoldenCaseEdited = "curation.golden_case_edited";
    public const string RelationshipVerified = "curation.relationship_verified";
    public const string RelationshipUnverified = "curation.relationship_unverified";
    public const string RelationshipEdited = "curation.relationship_edited";
    public const string RelationshipDeleted = "curation.relationship_deleted";
    public const string GroundingRechecked = "curation.grounding_rechecked";
    public const string RelationshipCreated = "curation.relationship_created";
    public const string GlossaryTermCreated = "curation.glossary_term_created";
    public const string GlossaryTermUpdated = "curation.glossary_term_updated";
    public const string GlossaryTermDeleted = "curation.glossary_term_deleted";
    public const string DocumentationSectionEdited = "curation.documentation_section_edited";
    public const string DocumentationGenerationStarted = "curation.documentation_generation_started";

    // Data quality
    public const string ContractCreated = "data_quality.contract_created";
    public const string ContractUpdated = "data_quality.contract_updated";
    public const string ContractArchived = "data_quality.contract_archived";
    public const string ContractEvaluated = "data_quality.contract_evaluated";

    // Data migrations
    public const string MigrationJobCreated = "migrations.job_created";
    public const string MigrationJobDeleted = "migrations.job_deleted";
    public const string MigrationJobRunRequested = "migrations.job_run_requested";
    public const string MigrationJobStarted = "migrations.job_started";
    public const string MigrationJobRunEnded = "migrations.job_run_ended";

    // Embedding
    public const string EmbedGrantCreated = "embed.grant_created";
    public const string EmbedGrantRevoked = "embed.grant_revoked";
    public const string EmbedTokenIssued = "embed.token_issued";
    public const string EmbedTokenRefused = "embed.token_refused";
    public const string EmbedQueryRendered = "embed.query_rendered";

    // Row security
    public const string AttributeDefined = "row_security.attribute_defined";
    public const string AttributeUpdated = "row_security.attribute_updated";
    public const string AttributeArchived = "row_security.attribute_archived";
    public const string AttributeValuesSet = "row_security.attribute_values_set";
    public const string RowPolicyCreated = "row_security.policy_created";
    public const string RowPolicyUpdated = "row_security.policy_updated";
    public const string RowPolicyArchived = "row_security.policy_archived";
    public const string TableClassified = "row_security.table_classified";

    // The audit itself
    public const string AuditRead = "audit.read";
    public const string AuditVerified = "audit.verified";
    public const string AuditPurged = "audit.purged";
    public const string McpAuditRead = "audit.mcp_audit_read";
    public const string McpAuditVerified = "audit.mcp_audit_verified";
    public const string McpAuditSealingStarted = "audit.mcp_audit_sealing_started";
    public const string ExecutionLogVerified = "audit.execution_log_verified";
    public const string ExecutionLogSealingStarted = "audit.execution_log_sealing_started";
    public const string SettingHistoryVerified = "audit.setting_history_verified";
    public const string SettingHistorySealingStarted = "audit.setting_history_sealing_started";

    /// <summary>Every action in the catalogue.</summary>
    public static IReadOnlySet<string> All { get; } = typeof(SecurityAuditActions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(x => x.IsLiteral)
        .Select(x => (string)x.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The category of <paramref name="action"/>: the part before its first dot.</summary>
    public static string CategoryOf(string action)
    {
        var dot = action.IndexOf('.', StringComparison.Ordinal);

        return dot > 0 ? action[..dot] : action;
    }
}
