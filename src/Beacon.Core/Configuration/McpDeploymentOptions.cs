using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Deployment-level MCP guarantees bound from <c>Beacon:Mcp</c>. Locks pin a setting for the global row and
/// every project regardless of what an admin stores — an Admin included (the UI disables the switch, the API answers
/// 409); only the host's configuration lifts one. Ceilings bound the numeric settings — a stored value above a ceiling
/// resolves to the ceiling. An absent section means the read-only and PII-detection locks are on, content retention and
/// row data to the LLM are not locked, and there are no ceilings beyond the built-in SQL / question length caps and the
/// built-in <c>ask</c> time and LLM-call budgets.
/// </summary>
public sealed class McpDeploymentOptions
{
    public const string SectionName = "Beacon:Mcp";

    /// <summary>
    /// Pins <c>EnforceReadOnly = true</c> everywhere, and makes the execution gate enforce read-only whatever a caller
    /// asks for. On by default; set it to <c>false</c> to let an Admin turn read-only enforcement off at runtime.
    /// </summary>
    public bool ForceReadOnly { get; set; } = true;

    /// <summary>
    /// Pins <c>EnablePiiDetection = true</c> everywhere. On by default; set it to <c>false</c> to let an Admin turn PII
    /// detection off at runtime.
    /// </summary>
    public bool ForcePiiDetection { get; set; } = true;

    /// <summary>Pins <c>RetainQueryContent = false</c> everywhere.</summary>
    public bool ForceNoContentRetention { get; set; }

    /// <summary>
    /// Pins <c>EnableSampleValueCollection</c>, <c>EnableValueGrounding</c> and <c>EnableEvalJudge</c> to <c>false</c>
    /// everywhere, so no row value read from a data source goes to the LLM provider: no sample values are collected or
    /// shown, no value is probed for grounding, documentation discovers no column values, and no eval judge sees result
    /// rows. Off by default.
    /// </summary>
    public bool ForbidRowDataToLlm { get; set; }

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

    /// <summary>
    /// Pins <c>RetainQueryContent = true</c> everywhere, so no Admin can switch audit content off at runtime: the MCP audit
    /// keeps the question and SQL of every call (and the statements it ran) until its retention purges them. Off by
    /// default; contradicts <c>Beacon:Mcp:ForceNoContentRetention</c>, and the host fails to start with both on.
    /// </summary>
    public bool RequireContent { get; set; }
}

/// <summary>
/// Upper bounds project or global settings cannot exceed. <c>null</c> = no ceiling, except for the input caps
/// <see cref="MaxSqlChars"/> and <see cref="MaxQuestionChars"/> and the <c>ask</c> budgets <see cref="AskTimeoutSeconds"/>
/// and <see cref="AskMaxLlmCalls"/>, which always apply and fall back to a built-in default.
/// </summary>
public sealed class McpCeilingOptions
{
    internal const int DefaultMaxSqlChars = 100_000;
    internal const int DefaultMaxQuestionChars = 4_000;
    internal const int DefaultAskTimeoutSeconds = 120;
    internal const int DefaultAskMaxLlmCalls = 12;
    internal const int MaxAskTimeoutSeconds = 3_600;

    // A data question needs at least the intent classification and the first SQL generation.
    internal const int MinAskMaxLlmCalls = 2;

    public int? MaxRowLimit { get; set; }
    public int? StatementTimeoutSeconds { get; set; }
    public int? MaxResultBytes { get; set; }
    public decimal? MaxExplainCost { get; set; }
    public int? MaxConcurrentQueriesPerKey { get; set; }

    /// <summary>Longest SQL text the execution gate evaluates; <c>null</c> = 100 000 characters.</summary>
    public int? MaxSqlChars { get; set; }

    /// <summary>Longest question the <c>ask</c> tool accepts; <c>null</c> = 4 000 characters.</summary>
    public int? MaxQuestionChars { get; set; }

    /// <summary>
    /// Wall-clock time one <c>ask</c> call may take, from its start to its answer; <c>null</c> = 120 seconds. 1 to 3 600.
    /// </summary>
    public int? AskTimeoutSeconds { get; set; }

    /// <summary>
    /// Logical LLM calls one <c>ask</c> call may make, all steps together (a provider's own transport retries are not
    /// counted); <c>null</c> = 12. At least 2: the intent classification and the first SQL generation.
    /// </summary>
    public int? AskMaxLlmCalls { get; set; }

    public int EffectiveMaxSqlChars => MaxSqlChars ?? DefaultMaxSqlChars;

    public int EffectiveMaxQuestionChars => MaxQuestionChars ?? DefaultMaxQuestionChars;

    public int EffectiveAskTimeoutSeconds => AskTimeoutSeconds ?? DefaultAskTimeoutSeconds;

    public int EffectiveAskMaxLlmCalls => AskMaxLlmCalls ?? DefaultAskMaxLlmCalls;
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

        if (ceilings.AskTimeoutSeconds is <= 0 or > McpCeilingOptions.MaxAskTimeoutSeconds)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Ceilings:{nameof(McpCeilingOptions.AskTimeoutSeconds)} must be between 1 and {McpCeilingOptions.MaxAskTimeoutSeconds} when set.");
        }

        if (ceilings.AskMaxLlmCalls < McpCeilingOptions.MinAskMaxLlmCalls)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Ceilings:{nameof(McpCeilingOptions.AskMaxLlmCalls)} must be at least {McpCeilingOptions.MinAskMaxLlmCalls} when set (the intent classification and the first SQL generation).");
        }

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

        // The two locks pin RetainQueryContent in opposite directions: no stored value could satisfy both.
        if (audit.RequireContent && options.ForceNoContentRetention)
        {
            failures.Add(
                $"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.RequireContent)} and {McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.ForceNoContentRetention)} cannot both be on: one pins query content retention on, the other off.");
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
