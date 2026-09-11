using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Deployment-level MCP guarantees bound from <c>Beacon:Mcp</c>. Locks pin a setting for the global row and
/// every project regardless of what an admin stores (the UI hides the toggle, the API answers 409); ceilings
/// bound the numeric settings — a stored value above a ceiling resolves to the ceiling. An absent section means
/// no locks and no ceilings, which is today's behaviour.
/// </summary>
public sealed class McpDeploymentOptions
{
    public const string SectionName = "Beacon:Mcp";

    /// <summary>Pins <c>EnforceReadOnly = true</c> everywhere (R2).</summary>
    public bool ForceReadOnly { get; set; }

    /// <summary>Pins <c>RetainQueryContent = false</c> everywhere (R1).</summary>
    public bool ForceNoContentRetention { get; set; }

    public McpCeilingOptions Ceilings { get; set; } = new();
}

/// <summary>Upper bounds project or global settings cannot exceed. <c>null</c> = no ceiling.</summary>
public sealed class McpCeilingOptions
{
    public int? MaxRowLimit { get; set; }
    public int? StatementTimeoutSeconds { get; set; }
    public int? MaxResultBytes { get; set; }
    public decimal? MaxExplainCost { get; set; }
    public int? MaxConcurrentQueriesPerKey { get; set; }
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

        if (ceilings.MaxExplainCost is <= 0)
        {
            failures.Add($"{McpDeploymentOptions.SectionName}:Ceilings:{nameof(McpCeilingOptions.MaxExplainCost)} must be greater than zero when set.");
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
}
