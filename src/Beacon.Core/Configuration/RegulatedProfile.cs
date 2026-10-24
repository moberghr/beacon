using Microsoft.Extensions.Configuration;
using Beacon.Core.Authentication;
using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>One setting weaker than the regulated profile requires: its configuration key and what the profile requires of it.</summary>
public sealed record DeploymentProfileFinding(string Setting, string Requirement);

/// <summary>
/// The regulated deployment profile (<c>Beacon:Profile = Regulated</c>). Each setting is either filled by the profile when
/// the host's configuration leaves its key unset, or required as it stands; a host value weaker than the profile requires
/// is never overridden: it fails startup, listed with every other one.
/// <list type="bullet">
/// <item>Filled when unset, refused when set weaker: <c>Beacon:Mcp:Audit:Required</c> and
/// <c>Beacon:Mcp:ForbidRowDataToLlm</c> (true), <c>Beacon:Retention:Locked</c> (true, so every retention window must then be
/// configured), and the MCP execution ceilings <c>Beacon:Mcp:Ceilings:MaxRowLimit</c>, <c>StatementTimeoutSeconds</c>,
/// <c>MaxResultBytes</c> and <c>MaxConcurrentQueriesPerKey</c> (the built-in MCP settings defaults; any value the host sets
/// is kept).</item>
/// <item>Already on by default, refused when set off: <c>Beacon:Mcp:ForceReadOnly</c>, <c>Beacon:Mcp:ForcePiiDetection</c>,
/// every <c>Beacon:RateLimits</c> limit and the password lockout.</item>
/// <item>Required, never filled (the host must decide): <c>Beacon:UserManagement:LocalLogin:Enabled</c> false (break-glass
/// accounts allowed), and, with single sign-on on, <c>Beacon:Authentication:Oidc:RequireMfa</c> true.</item>
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
    internal const string LocalLoginEnabledKey = $"{LocalLoginOptions.SectionName}:{nameof(LocalLoginOptions.Enabled)}";
    internal const string RequireMfaKey = $"Beacon:Authentication:Oidc:{nameof(OidcAuthenticationOptions.RequireMfa)}";

    /// <summary>The configuration keys the profile fills when the host leaves them unset, in report order.</summary>
    internal static readonly IReadOnlyList<string> DefaultedKeys =
    [
        AuditRequiredKey,
        ForbidRowDataToLlmKey,
        RetentionLockedKey,
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxRowLimit)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.StatementTimeoutSeconds)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxResultBytes)}",
        $"{CeilingsSection}:{nameof(McpCeilingOptions.MaxConcurrentQueriesPerKey)}"
    ];

    /// <summary>The keys of <see cref="DefaultedKeys"/> the host's configuration leaves unset.</summary>
    internal static IReadOnlyList<string> UnsetDefaults(IConfiguration configuration)
    {
        return DefaultedKeys
            .Where(x => configuration[x] == null)
            .ToList();
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

    /// <summary>Every setting weaker than the regulated profile requires; empty when all of them hold.</summary>
    public static IReadOnlyList<DeploymentProfileFinding> Assess(
        McpDeploymentOptions mcp,
        RetentionOptions retention,
        RateLimitOptions rateLimits,
        OidcAuthenticationOptions? oidc,
        LocalLoginOptions localLogin)
    {
        var findings = new List<DeploymentProfileFinding>();

        if (!mcp.ForceReadOnly)
        {
            findings.Add(new(ForceReadOnlyKey, "must be true: an Admin can turn MCP read-only enforcement off at runtime"));
        }

        if (!mcp.ForcePiiDetection)
        {
            findings.Add(new(ForcePiiDetectionKey, "must be true: an Admin can turn MCP PII detection off at runtime"));
        }

        if (mcp.Audit?.Required != true)
        {
            findings.Add(new(AuditRequiredKey, "must be true: an MCP call whose audit row cannot be written still runs"));
        }

        if (!mcp.ForbidRowDataToLlm)
        {
            findings.Add(new(ForbidRowDataToLlmKey, "must be true: values read from data sources can reach the LLM provider"));
        }

        var ceilings = mcp.Ceilings ?? new McpCeilingOptions();
        AddCeiling(findings, nameof(McpCeilingOptions.MaxRowLimit), ceilings.MaxRowLimit);
        AddCeiling(findings, nameof(McpCeilingOptions.StatementTimeoutSeconds), ceilings.StatementTimeoutSeconds);
        AddCeiling(findings, nameof(McpCeilingOptions.MaxResultBytes), ceilings.MaxResultBytes);
        AddCeiling(findings, nameof(McpCeilingOptions.MaxConcurrentQueriesPerKey), ceilings.MaxConcurrentQueriesPerKey);

        if (!retention.Locked)
        {
            findings.Add(new(RetentionLockedKey, "must be true: a retention window may keep run history, logs or the MCP audit forever"));
        }

        AddRateLimit(findings, nameof(RateLimitOptions.Login), rateLimits.Login);
        AddRateLimit(findings, nameof(RateLimitOptions.Api), rateLimits.Api);
        AddRateLimit(findings, nameof(RateLimitOptions.Anonymous), rateLimits.Anonymous);
        AddRateLimit(findings, nameof(RateLimitOptions.Mcp), rateLimits.Mcp);
        AddRateLimit(findings, nameof(RateLimitOptions.Ask), rateLimits.Ask);

        if (rateLimits.Lockout?.IsEnabled != true)
        {
            findings.Add(new(
                $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Lockout)}",
                "must be on (Enabled true, MaxFailedAttempts greater than zero): password guessing is limited only per client address"));
        }

        if (localLogin.Enabled)
        {
            findings.Add(new(LocalLoginEnabledKey, "must be false: every account can sign in with a password (list emergency accounts in BreakGlassUserNames)"));
        }

        if (oidc?.Enabled == true && !oidc.RequireMfa)
        {
            findings.Add(new(RequireMfaKey, "must be true: single sign-on admits sign-ins without multi-factor authentication (check the amr/acr claims your identity provider sends first)"));
        }

        return findings;
    }

    private static void AddCeiling(List<DeploymentProfileFinding> findings, string ceiling, int? value)
    {
        if (value == null)
        {
            findings.Add(new($"{CeilingsSection}:{ceiling}", "must be set: an Admin can raise the MCP setting to the built-in maximum"));
        }
    }

    private static void AddRateLimit(List<DeploymentProfileFinding> findings, string limit, RateLimitWindow? window)
    {
        if (window?.IsEnabled != true)
        {
            findings.Add(new($"{RateLimitOptions.SectionName}:{limit}:{nameof(RateLimitWindow.PermitLimit)}", "must be greater than zero: the limit is off"));
        }
    }
}
