using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Deployment-level MCP guarantees bound from <c>Beacon:Mcp</c>. Locks pin a setting for the global row and
/// every project regardless of what an admin stores (the UI hides the toggle, the API answers 409); ceilings
/// bound the numeric settings — a stored value above a ceiling resolves to the ceiling. An absent section means
/// no locks, no ceilings and the built-in SQL / question length caps.
/// </summary>
public sealed class McpDeploymentOptions
{
    public const string SectionName = "Beacon:Mcp";

    /// <summary>Pins <c>EnforceReadOnly = true</c> everywhere (R2).</summary>
    public bool ForceReadOnly { get; set; }

    /// <summary>Pins <c>RetainQueryContent = false</c> everywhere (R1).</summary>
    public bool ForceNoContentRetention { get; set; }

    public McpCeilingOptions Ceilings { get; set; } = new();

    public McpAuditOptions Audit { get; set; } = new();
}

/// <summary>Audit guarantees bound from <c>Beacon:Mcp:Audit</c>. Defaults reproduce today's behaviour.</summary>
public sealed class McpAuditOptions
{
    public const string DefaultRequestIdHeader = "X-Request-Id";

    /// <summary>Fail closed: withhold a tool result whose audit row could not be written.</summary>
    public bool Required { get; set; }

    /// <summary>Days audit rows are kept. <c>null</c> = keep forever; must be greater than zero when set.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Upstream correlation header recorded as <c>UpstreamRequestId</c>. <c>null</c>/empty = read none.</summary>
    public string? RequestIdHeader { get; set; } = DefaultRequestIdHeader;
}

/// <summary>
/// Upper bounds project or global settings cannot exceed. <c>null</c> = no ceiling, except for the input caps
/// <see cref="MaxSqlChars"/> and <see cref="MaxQuestionChars"/>, which always apply and fall back to a built-in default.
/// </summary>
public sealed class McpCeilingOptions
{
    internal const int DefaultMaxSqlChars = 100_000;
    internal const int DefaultMaxQuestionChars = 4_000;

    public int? MaxRowLimit { get; set; }
    public int? StatementTimeoutSeconds { get; set; }
    public int? MaxResultBytes { get; set; }
    public decimal? MaxExplainCost { get; set; }
    public int? MaxConcurrentQueriesPerKey { get; set; }

    /// <summary>Longest SQL text the execution gate evaluates; <c>null</c> = 100 000 characters.</summary>
    public int? MaxSqlChars { get; set; }

    /// <summary>Longest question the <c>ask</c> tool accepts; <c>null</c> = 4 000 characters.</summary>
    public int? MaxQuestionChars { get; set; }

    public int EffectiveMaxSqlChars => MaxSqlChars ?? DefaultMaxSqlChars;

    public int EffectiveMaxQuestionChars => MaxQuestionChars ?? DefaultMaxQuestionChars;
}

internal sealed class McpDeploymentOptionsValidator : IValidateOptions<McpDeploymentOptions>
{
    public ValidateOptionsResult Validate(string? name, McpDeploymentOptions options)
    {
        var failures = new List<string>();
        var ceilings = options.Ceilings ?? new McpCeilingOptions();

        RequirePositive(failures, nameof(McpCeilingOptions.MaxRowLimit), ceilings.MaxRowLimit);
        RequirePositive(failures, nameof(McpCeilingOptions.StatementTimeoutSeconds), ceilings.StatementTimeoutSeconds);
        RequirePositive(failures, nameof(McpCeilingOptions.MaxResultBytes), ceilings.MaxResultBytes);
        RequirePositive(failures, nameof(McpCeilingOptions.MaxConcurrentQueriesPerKey), ceilings.MaxConcurrentQueriesPerKey);
        RequirePositive(failures, nameof(McpCeilingOptions.MaxSqlChars), ceilings.MaxSqlChars);
        RequirePositive(failures, nameof(McpCeilingOptions.MaxQuestionChars), ceilings.MaxQuestionChars);

        if (ceilings.MaxExplainCost is <= 0)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Ceilings:{nameof(McpCeilingOptions.MaxExplainCost)} must be greater than zero when set.");
        }

        var audit = options.Audit ?? new McpAuditOptions();

        if (audit.RetentionDays is <= 0)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.RetentionDays)} must be greater than zero when set.");
        }

        if (!string.IsNullOrEmpty(audit.RequestIdHeader) && !IsHeaderToken(audit.RequestIdHeader))
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.RequestIdHeader)} must be a valid HTTP header name.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void RequirePositive(List<string> failures, string field, int? value)
    {
        if (value is <= 0)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Ceilings:{field} must be greater than zero when set.");
        }
    }

    // RFC 7230 §3.2.6 token: one or more tchar.
    private static bool IsHeaderToken(string value)
    {
        foreach (var x in value)
        {
            var isTchar = x is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                || "!#$%&'*+-.^_`|~".Contains(x);

            if (!isTchar)
            {
                return false;
            }
        }

        return true;
    }
}
