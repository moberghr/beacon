using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// What a query step's SQL must pass before it is stored — by a save from the query editor or by an AI actor creating or
/// refining a query: the shared execution gate in the data source's dialect, as the SQL will run (placeholders bound) —
/// regex guardrail, a single read-only statement, names in the source's own configured database only, the host policy for
/// a host data source — and, when Beacon holds metadata for the source, every column the SQL reads from a table in that
/// metadata must exist there (a blocking catalog check; a table the metadata does not list is left to the database). Every execution re-checks the read-only stages; this keeps SQL that could never run, or
/// that names objects the source does not have, from being stored at all. Registered with <c>TryAdd</c>.
/// </summary>
public interface IStepSqlSaveCheck
{
    /// <param name="placeholders">The step's parameter placeholders, bound the way execution binds them.</param>
    /// <returns>Null when the SQL may be stored; the reason it may not otherwise. A missing data source is a reason.</returns>
    Task<string?> RejectionAsync(int dataSourceId, string sql, IReadOnlyCollection<string> placeholders, CancellationToken cancellationToken);
}

internal sealed class StepSqlSaveCheck(
    IDbContextFactory<BeaconContext> contextFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases) : IStepSqlSaveCheck
{
    public async Task<string?> RejectionAsync(int dataSourceId, string sql, IReadOnlyCollection<string> placeholders, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken);
        if (dataSource == null)
        {
            return $"Data source {dataSourceId} not found.";
        }

        var bindings = placeholders
            .Select(x =>
                new SubscriptionParameterData
                {
                    QueryPlaceholder = x
                })
            .ToList();
        var (boundSql, _) = QueryHelper.PrepareParameterizedQuery(sql, bindings);
        var request = new SqlGateRequest(
            StoredSql.Flatten(boundSql, DataSourceSqlDialect.Of(dataSource)),
            DataSourceSqlDialect.Of(dataSource),
            EnforceReadOnly: true,
            DetectPii: false,
            CustomPiiPatterns: null,
            Catalog: null,
            BlockOnSchemaFailure: true,
            LintContext: null,
            EnableSemanticLint: false,
            MaxRows: null)
        {
            HostManagedKey = dataSource.HostManagedKey,
            ConfiguredDatabase = configuredDatabases.Resolve(dataSource)
        };

        // The read-only stages first: SQL that may never run is refused without reading the catalog.
        var readOnly = gate.Evaluate(request);
        if (readOnly.Blocked)
        {
            return readOnly.BlockReason ?? "The SQL was rejected.";
        }

        var tables = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .Select(x =>
                new
                {
                    x.SchemaName,
                    x.TableName,
                    Columns = x.Columns
                        .Select(y => y.ColumnName)
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        // Keyed as the MCP catalog is: the table alone and schema.table, lower case, each to its lower-case columns.
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var columns = table.Columns
                .Select(x => x.ToLowerInvariant())
                .ToHashSet();
            catalog[table.TableName.ToLowerInvariant()] = columns;
            catalog[$"{table.SchemaName}.{table.TableName}".ToLowerInvariant()] = columns;
        }

        // The catalog stage reads names, and on some dialects a bind marker reads as one (PostgreSQL parses @p0 as abs(p0)),
        // so it judges the statement with each placeholder written as NULL instead.
        var catalogSql = placeholders
            .Where(x => !string.IsNullOrEmpty(x))
            .Aggregate(sql, (current, placeholder) => current.Replace(placeholder, "NULL"));
        var report = gate.Evaluate(request with { Sql = StoredSql.Flatten(catalogSql, request.Dialect), Catalog = catalog });

        return report.Blocked ? report.BlockReason ?? "The SQL was rejected." : null;
    }
}

/// <summary>The text a stored step runs as.</summary>
internal static class StoredSql
{
    /// <summary>
    /// The exact text a stored step runs: its line comments removed as the dialect reads them
    /// (<see cref="SqlCommentStripper.StripLineComments"/>), then newline, carriage return and tab replaced by a space.
    /// Removing the line comments first keeps a comment from swallowing the rest of the statement once it is on one line.
    /// Where they cannot be removed safely (an unknown dialect, SQL the tokenizer cannot read) a <c>--</c> comment still
    /// swallows the rest, so every check judges this form, never the original.
    /// </summary>
    public static string Flatten(string sql, string? dialect)
    {
        return SqlCommentStripper.StripLineComments(sql, dialect)
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();
    }
}
