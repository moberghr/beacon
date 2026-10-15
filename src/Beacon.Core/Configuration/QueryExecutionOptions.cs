using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// How much one statement Beacon runs for its own features may return, and how long it may run, bound from
/// <c>Beacon:Query</c>.
/// </summary>
public sealed class QueryExecutionOptions
{
    public const string SectionName = "Beacon:Query";

    /// <summary>The largest value accepted. Every row is held in memory while a result is processed.</summary>
    public const int MaxAllowedExecutionRows = 10_000_000;

    /// <summary>The largest <see cref="StatementTimeoutSeconds"/> accepted: one hour.</summary>
    public const int MaxAllowedStatementTimeoutSeconds = 3600;

    /// <summary>
    /// The most rows one statement of a query preview, subscription run, AI actor check or ad-hoc query may return (a
    /// migration's read has its own budget, <see cref="MigrationOptions.MaxRows"/>). The cap is written into the SQL and the
    /// read stops one row past it; a larger result fails the run, it is never cut short. Default 100,000; between 1 and
    /// <see cref="MaxAllowedExecutionRows"/>.
    /// </summary>
    public int MaxExecutionRows { get; set; } = Constants.Query.MaxExecutionRows;

    /// <summary>
    /// How long one such statement may run before it is cancelled — a step, a final query on the in-memory join store, a
    /// metadata sample. A subscription's own timeout may only be shorter: a longer one is refused when the subscription is
    /// saved. Default 120; between 1 and <see cref="MaxAllowedStatementTimeoutSeconds"/>.
    /// </summary>
    public int StatementTimeoutSeconds { get; set; } = Constants.Query.StatementTimeoutSeconds;
}

internal sealed class QueryExecutionOptionsValidator : IValidateOptions<QueryExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, QueryExecutionOptions options)
    {
        var failures = new List<string>();
        if (options.MaxExecutionRows is < 1 or > QueryExecutionOptions.MaxAllowedExecutionRows)
        {
            failures.Add($"{QueryExecutionOptions.SectionName}:{nameof(QueryExecutionOptions.MaxExecutionRows)} must be between 1 and {QueryExecutionOptions.MaxAllowedExecutionRows}.");
        }

        if (options.StatementTimeoutSeconds is < 1 or > QueryExecutionOptions.MaxAllowedStatementTimeoutSeconds)
        {
            failures.Add($"{QueryExecutionOptions.SectionName}:{nameof(QueryExecutionOptions.StatementTimeoutSeconds)} must be between 1 and {QueryExecutionOptions.MaxAllowedStatementTimeoutSeconds}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
