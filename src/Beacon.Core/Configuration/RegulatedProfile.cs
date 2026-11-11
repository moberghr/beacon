using Microsoft.Extensions.Configuration;
using Beacon.Core.Authentication;
using Beacon.Core.Models;
using Beacon.Core.Services.Embed;

namespace Beacon.Core.Configuration;

/// <summary>One setting weaker than the regulated profile requires: its configuration key and what the profile requires of it.</summary>
public sealed record DeploymentProfileFinding(string Setting, string Requirement);

/// <summary>
/// The regulated deployment profile (<c>Beacon:Profile = Regulated</c>). Each setting is either filled by the profile when
/// the host's configuration leaves its key unset, or required as it stands; a host value weaker than the profile requires
/// is never overridden: it fails startup, listed with every other one. The requirements are the list
/// <see cref="Assess(RegulatedProfileSettings)"/> walks, in these groups: the MCP locks and ceilings, retention, request
/// limits and password guessing, sign-in and access, the browser protections, the approval workflow, the audit and
/// encryption, and the data-source and LLM connections.
/// <list type="bullet">
/// <item>Filled when unset (<see cref="DefaultedKeys"/>), refused when set weaker: the MCP audit, row-data and ceiling
/// settings, <c>Beacon:Retention:Locked</c>, <c>Beacon:Audit:Required</c>, <c>Beacon:Audit:ExecutionLog:Required</c>,
/// <c>Beacon:Authentication:Cookie:SecureAlways</c> and <c>Beacon:UserManagement:MinimumPasswordLength</c> (12).</item>
/// <item>Already strong by default, refused when set weaker: the MCP locks, every rate limit, the lockout and break-glass
/// delay, the Content-Security-Policy, the approval workflow's default switches and the switches that lift a transport,
/// credential, endpoint or key-ring check.</item>
/// <item>Required, never filled (the host must decide): local login off, MFA with single sign-on, user management, project
/// membership, API key expiry for keys stored without one, the origin check, the public base URL, the approval workflow
/// and its other switches, the audit integrity key, the legacy encryption format off, the transport check on stored
/// connections, and a data-source host list or no private networks.</item>
/// </list>
/// The same checks make the startup report of weak settings, also without a profile.
/// </summary>
public static class RegulatedProfile
{
    internal const string ForceReadOnlyKey = $"{McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.ForceReadOnly)}";
    internal const string ForcePiiDetectionKey = $"{McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.ForcePiiDetection)}";
    internal const string ForbidRowDataToLlmKey = $"{McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.ForbidRowDataToLlm)}";
    internal const string AuditRequiredKey = $"{McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.Audit)}:{nameof(McpAuditOptions.Required)}";
    internal const string CeilingsSection = $"{McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.Ceilings)}";
    internal const string RetentionLockedKey = $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.Locked)}";
    internal const string LockoutKey = $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Lockout)}";
    internal const string LocalLoginEnabledKey = $"{LocalLoginOptions.SectionName}:{nameof(LocalLoginOptions.Enabled)}";
    internal const string RequireMfaKey = $"Beacon:Authentication:Oidc:{nameof(OidcAuthenticationOptions.RequireMfa)}";
    internal const string UserManagementEnabledSetting = $"{nameof(BeaconConfiguration.UserManagement)}.{nameof(UserManagementOptions.Enabled)}";
    internal const string RequireProjectMembershipKey = $"{ProjectAuthorizationOptions.SectionName}:{nameof(ProjectAuthorizationOptions.RequireProjectMembership)}";
    internal const string MinimumPasswordLengthKey = UserManagementOptions.MinimumPasswordLengthKey;
    internal const string ApiKeyExpiryKey = $"{ApiKeyOptions.SectionName}:{nameof(ApiKeyOptions.EnforceMaxLifetimeOnExistingKeys)}";
    internal const string CookieSecureAlwaysKey = "Beacon:Authentication:Cookie:SecureAlways";
    internal const string AllowUnencryptedKeysKey = "Beacon:DataProtection:AllowUnencryptedKeys";
    internal const string CspKey = "Beacon:Security:Csp";
    internal const string OriginGuardKey = "Beacon:Security:OriginGuard";
    internal const string PublicBaseUrlKey = "Beacon:PublicBaseUrl";
    internal const string ApprovalWorkflowSetting = nameof(BeaconConfiguration.ApprovalWorkflow);
    internal const string SecurityAuditRequiredKey = $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.Required)}";
    internal const string ExecutionLogRequiredKey = $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.ExecutionLog)}:{nameof(ExecutionLogOptions.Required)}";
    internal const string IntegrityKeyKey = $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.IntegrityKey)}";
    internal const string AllowLegacyCbcKey = ServiceConfiguration.AllowLegacyCbcKey;
    internal const string EnforceOnExistingKey = $"{DataSourceTransportOptions.SectionName}:{nameof(DataSourceTransportOptions.EnforceOnExisting)}";
    internal const string AllowInsecureTransportKey = $"{DataSourceTransportOptions.SectionName}:{nameof(DataSourceTransportOptions.AllowInsecure)}";
    internal const string AllowHostCredentialsKey = $"{DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowHostCredentials)}";
    internal const string AllowedHostsKey = $"{DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)}";
    internal const string AllowPrivateNetworksKey = $"{DataSourceOutboundOptions.SectionName}:{nameof(DataSourceOutboundOptions.AllowPrivateNetworks)}";
    internal const string AllowInsecureEndpointKey = $"{LlmEndpointOptions.SectionName}:{nameof(LlmEndpointOptions.AllowInsecureEndpoint)}";
    internal const string EmbedEnabledKey = $"{EmbedOptions.SectionName}:{nameof(EmbedOptions.Enabled)}";
    internal const string MigrationsEnabledKey = $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.Enabled)}";

    /// <summary>The shortest password the profile accepts, and the length it fills.</summary>
    internal const int MinimumPasswordLength = 12;

    private const string Enforce = "Enforce";

    /// <summary>The configuration keys the profile fills when the host leaves them unset, in report order.</summary>
    internal static readonly IReadOnlyList<string> DefaultedKeys =
    [
        AuditRequiredKey,
        ForbidRowDataToLlmKey,
        RetentionLockedKey,
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxRowLimit)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.StatementTimeoutSeconds)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxResultBytes)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxConcurrentQueriesPerKey)}",
        SecurityAuditRequiredKey,
        ExecutionLogRequiredKey,
        CookieSecureAlwaysKey,
        MinimumPasswordLengthKey
    ];

    // One entry per requirement: the setting it names, what it requires and why, and whether the settings meet it.
    private static readonly IReadOnlyList<Requirement> Requirements =
    [
        // The MCP locks and ceilings.
        new(ForceReadOnlyKey, "must be true: an Admin can turn MCP read-only enforcement off at runtime", x => x.Mcp.ForceReadOnly),
        new(ForcePiiDetectionKey, "must be true: an Admin can turn MCP PII detection off at runtime", x => x.Mcp.ForcePiiDetection),
        new(AuditRequiredKey, "must be true: an MCP call whose audit row cannot be written still runs", x => x.Mcp.Audit?.Required == true),
        new(ForbidRowDataToLlmKey, "must be true: values read from data sources can reach the LLM provider", x => x.Mcp.ForbidRowDataToLlm),
        Ceiling(nameof(McpCeilingOptions.MaxRowLimit), x => x.MaxRowLimit),
        Ceiling(nameof(McpCeilingOptions.StatementTimeoutSeconds), x => x.StatementTimeoutSeconds),
        Ceiling(nameof(McpCeilingOptions.MaxResultBytes), x => x.MaxResultBytes),
        Ceiling(nameof(McpCeilingOptions.MaxConcurrentQueriesPerKey), x => x.MaxConcurrentQueriesPerKey),

        // Retention.
        new(RetentionLockedKey, "must be true: a retention window may keep run history, logs or the MCP audit forever", x => x.Retention.Locked),

        // Request limits and password guessing.
        RateLimit(nameof(RateLimitOptions.Login), x => x.Login),
        RateLimit(nameof(RateLimitOptions.Api), x => x.Api),
        RateLimit(nameof(RateLimitOptions.Anonymous), x => x.Anonymous),
        RateLimit(nameof(RateLimitOptions.Mcp), x => x.Mcp),
        RateLimit(nameof(RateLimitOptions.Ask), x => x.Ask),
        new(LockoutKey, "must be on (Enabled true, MaxFailedAttempts greater than zero): password guessing is limited only per client address", x => x.RateLimits.Lockout?.IsEnabled == true),

        // Sign-in and access.
        new(LocalLoginEnabledKey, "must be false: every account can sign in with a password (list emergency accounts in BreakGlassUserNames)", x => !x.Beacon.UserManagement.LocalLogin.Enabled),
        new(RequireMfaKey, "must be true: single sign-on admits sign-ins without multi-factor authentication (check the amr/acr claims your identity provider sends first)", x => x.Oidc?.Enabled != true || x.Oidc.RequireMfa),
        new(UserManagementEnabledSetting, "must be true (in code, EnableUserManagement): without Beacon's user store there are no per-user roles, functional permissions or project memberships", x => x.Beacon.UserManagement.Enabled),
        new(RequireProjectMembershipKey, "must be true: a project open to everyone is readable by every signed-in user", x => x.ProjectAuthorization.RequireProjectMembership),
        new(MinimumPasswordLengthKey, $"must be at least {MinimumPasswordLength}: shorter internal passwords are accepted", x => x.Beacon.UserManagement.MinimumPasswordLength >= MinimumPasswordLength),
        new(ApiKeyExpiryKey, "must be true: an API key stored without an expiry never expires", x => x.ApiKeys.EnforceMaxLifetimeOnExistingKeys),
        new(CookieSecureAlwaysKey, "must be true: the session cookie is issued without the Secure flag on a request that arrives over http", x => IsTrue(x.Configuration, CookieSecureAlwaysKey) || Fills(x.Configuration, CookieSecureAlwaysKey)),
        new(AllowUnencryptedKeysKey, "must not be true: the keys that protect session cookies may be stored unencrypted", x => !IsTrue(x.Configuration, AllowUnencryptedKeysKey)),

        // The browser protections.
        new(CspKey, $"must be {Enforce} (or unset): browsers do not block what Beacon's Content-Security-Policy refuses", x => x.Configuration[CspKey] == null || IsEnforce(x.Configuration, CspKey)),
        new(OriginGuardKey, $"must be {Enforce} (list the origins browsers use in Beacon:Security:AllowedOrigins first): a request from another origin is only logged", x => IsEnforce(x.Configuration, OriginGuardKey)),
        new(PublicBaseUrlKey, "must be set to Beacon's https address: links, discovery documents and the origin check otherwise follow the request's Host header", x => IsHttpsUrl(x.Configuration[PublicBaseUrlKey])),

        // The approval workflow.
        new($"{ApprovalWorkflowSetting}.{nameof(ApprovalWorkflowOptions.Enabled)}", "must be true (in code, EnableApprovalWorkflow): a change to a query's SQL goes live without a second person's approval", x => x.Beacon.ApprovalWorkflow.Enabled),
        Covers(nameof(ApprovalWorkflowOptions.CoversMigrationJobs), "a new or changed migration job runs", x => x.CoversMigrationJobs),
        Covers(nameof(ApprovalWorkflowOptions.CoversCustomSqlRules), "a new or changed Custom SQL rule is evaluated", x => x.CoversCustomSqlRules),
        Covers(nameof(ApprovalWorkflowOptions.CoversRecipientDestinations), "a new recipient or changed destination receives deliveries", x => x.CoversRecipientDestinations),
        Covers(nameof(ApprovalWorkflowOptions.CoversDataSourceConnections), "a new data source or changed connection connects", x => x.CoversDataSourceConnections),
        Covers(nameof(ApprovalWorkflowOptions.CoversLlmConnection), "a change of the LLM connection's provider or endpoint applies", x => x.CoversLlmConnection),
        Covers(nameof(ApprovalWorkflowOptions.CoversAdminRoleGrants), "a user is given the Admin role", x => x.CoversAdminRoleGrants),
        new($"{ApprovalWorkflowSetting}.{nameof(ApprovalWorkflowOptions.AllowSelfApproval)}", "must be false (in code): a requester can approve their own change", x => !x.Beacon.ApprovalWorkflow.AllowSelfApproval),

        // The audit and encryption.
        new(SecurityAuditRequiredKey, "must be true: a sign-in whose audit event cannot be written still gets a session", x => x.Audit.Required),
        new(ExecutionLogRequiredKey, "must be true: a statement whose execution log row cannot be written still runs", x => x.Audit.ExecutionLog?.Required == true),
        new(IntegrityKeyKey, "must be set, from a secret store: the audit trail is otherwise signed with a key derived from the encryption key, which changes with it", x => !string.IsNullOrWhiteSpace(x.Audit.IntegrityKey)),
        new(AllowLegacyCbcKey, "must be false (once the re-encryption run reports no value left in the legacy format): values in the legacy, unauthenticated format are still read", x => IsFalse(x.Configuration, AllowLegacyCbcKey)),

        // The data-source and LLM connections.
        new(EnforceOnExistingKey, "must be true: a stored connection that does not authenticate its server keeps connecting", x => x.Transport.EnforceOnExisting),
        new(AllowInsecureTransportKey, "must not be true: connections that do not authenticate their server are accepted", x => !x.Transport.AllowInsecure),
        new(AllowHostCredentialsKey, "must not be true: a connection can sign in as the Beacon host", x => !x.DataSources.AllowHostCredentials),
        new(AllowedHostsKey, $"must list the hosts data sources may reach, or {AllowPrivateNetworksKey} must be false: a connection can reach any host on the Beacon host's networks", x => x.DataSources.AllowedHosts.Count > 0 || !x.Outbound.AllowPrivateNetworks),
        new(AllowInsecureEndpointKey, "must not be true: an LLM endpoint over plain http is accepted", x => !x.Llm.AllowInsecureEndpoint)
    ];

    /// <summary>The keys of <see cref="DefaultedKeys"/> the host's configuration leaves unset.</summary>
    internal static IReadOnlyList<string> UnsetDefaults(IConfiguration configuration)
    {
        return DefaultedKeys
            .Where(x => configuration[x] == null)
            .ToList();
    }

    /// <summary>Whether the regulated profile fills <paramref name="key"/>: the profile is on and the host left the key unset.</summary>
    internal static bool Fills(IConfiguration configuration, string key)
    {
        return configuration[key] == null
            && DefaultedKeys.Contains(key, StringComparer.Ordinal)
            && DeploymentProfileOptions.From(configuration).IsRegulated;
    }

    /// <summary>Under the regulated profile, the minimum password length when the host's configuration leaves it unset.</summary>
    internal static int? FilledMinimumPasswordLength(IConfiguration configuration)
    {
        return Fills(configuration, MinimumPasswordLengthKey) ? MinimumPasswordLength : null;
    }

    /// <summary>Under the regulated profile, fills the MCP settings the host's configuration leaves unset.</summary>
    internal static void ApplyDefaults(McpDeploymentOptions options, IConfiguration configuration)
    {
        if (!DeploymentProfileOptions.From(configuration).IsRegulated)
        {
            return;
        }

        var unset = UnsetDefaults(configuration).ToHashSet(StringComparer.Ordinal);
        var builtIn = new McpSettingsData();
        options.Audit ??= new McpAuditOptions();
        options.Ceilings ??= new McpCeilingOptions();

        if (unset.Contains(AuditRequiredKey))
        {
            options.Audit.Required = true;
        }

        if (unset.Contains(ForbidRowDataToLlmKey))
        {
            options.ForbidRowDataToLlm = true;
        }

        options.Ceilings.MaxRowLimit ??= builtIn.MaxRowLimit;
        options.Ceilings.StatementTimeoutSeconds ??= builtIn.StatementTimeoutSeconds;
        options.Ceilings.MaxResultBytes ??= builtIn.MaxResultBytes;
        options.Ceilings.MaxConcurrentQueriesPerKey ??= builtIn.MaxConcurrentQueriesPerKey;
    }

    /// <summary>Under the regulated profile, turns the retention lock on when the host's configuration leaves it unset.</summary>
    internal static void ApplyDefaults(RetentionOptions options, IConfiguration configuration)
    {
        if (DeploymentProfileOptions.From(configuration).IsRegulated && configuration[RetentionLockedKey] == null)
        {
            options.Locked = true;
        }
    }

    /// <summary>
    /// Under the regulated profile, requires the sign-in audit event and the execution log row when the host's configuration
    /// leaves those switches unset.
    /// </summary>
    internal static void ApplyDefaults(SecurityAuditOptions options, IConfiguration configuration)
    {
        if (Fills(configuration, SecurityAuditRequiredKey))
        {
            options.Required = true;
        }

        if (Fills(configuration, ExecutionLogRequiredKey))
        {
            options.ExecutionLog ??= new ExecutionLogOptions();
            options.ExecutionLog.Required = true;
        }
    }

    /// <summary>Every setting weaker than the regulated profile requires; empty when all of them hold.</summary>
    public static IReadOnlyList<DeploymentProfileFinding> Assess(RegulatedProfileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Requirements
            .Where(x => !x.Holds(settings))
            .Select(x => new DeploymentProfileFinding(x.Setting, x.Text))
            .ToList();
    }

    /// <summary>
    /// Every setting weaker than the regulated profile requires, judging the given settings and Beacon's defaults for every
    /// other one; empty when all of them hold.
    /// </summary>
    [Obsolete("Judges Beacon's defaults for every setting it is not given; use Assess(RegulatedProfileSettings).")]
    public static IReadOnlyList<DeploymentProfileFinding> Assess(
        McpDeploymentOptions mcp,
        RetentionOptions retention,
        RateLimitOptions rateLimits,
        OidcAuthenticationOptions? oidc,
        LocalLoginOptions localLogin)
    {
        return Assess(new RegulatedProfileSettings
        {
            Mcp = mcp,
            Retention = retention,
            RateLimits = rateLimits,
            Oidc = oidc,
            Beacon = new BeaconConfiguration { UserManagement = new UserManagementOptions { LocalLogin = localLogin } }
        });
    }

    /// <summary>
    /// The surfaces the profile allows but a reviewer should know are on (the anonymous embed routes, data migration's write
    /// path), by configuration key; empty when none is.
    /// </summary>
    internal static IReadOnlyList<string> EnabledSurfaces(RegulatedProfileSettings settings)
    {
        var surfaces = new List<string>();
        if (settings.Embed.Enabled)
        {
            surfaces.Add(EmbedEnabledKey);
        }

        if (settings.Migrations.Enabled)
        {
            surfaces.Add(MigrationsEnabledKey);
        }

        return surfaces;
    }

    private static Requirement Ceiling(string ceiling, Func<McpCeilingOptions, int?> value)
    {
        return new(
            $"{CeilingsSection}:{ceiling}",
            "must be set: an Admin can raise the MCP setting to the built-in maximum",
            x => value(x.Mcp.Ceilings ?? new McpCeilingOptions()) != null);
    }

    private static Requirement RateLimit(string limit, Func<RateLimitOptions, RateLimitWindow?> window)
    {
        return new(
            $"{RateLimitOptions.SectionName}:{limit}:{nameof(RateLimitWindow.PermitLimit)}",
            "must be greater than zero: the limit is off",
            x => window(x.RateLimits)?.IsEnabled == true);
    }

    private static Requirement Covers(string option, string unreviewed, Func<ApprovalWorkflowOptions, bool> covers)
    {
        return new(
            $"{ApprovalWorkflowSetting}.{option}",
            $"must be true (in code): {unreviewed} without a second person's approval",
            x => covers(x.Beacon.ApprovalWorkflow));
    }

    // A switch Beacon reads from the host's configuration, true or false only when set to that value.
    private static bool IsTrue(IConfiguration configuration, string key)
    {
        return bool.TryParse(configuration[key], out var value) && value;
    }

    private static bool IsFalse(IConfiguration configuration, string key)
    {
        return bool.TryParse(configuration[key], out var value) && !value;
    }

    // A mode bound by name; a value given as a number is not read as one.
    private static bool IsEnforce(IConfiguration configuration, string key)
    {
        return string.Equals(configuration[key]?.Trim(), Enforce, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpsUrl(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }

    private sealed record Requirement(string Setting, string Text, Func<RegulatedProfileSettings, bool> Holds);
}
