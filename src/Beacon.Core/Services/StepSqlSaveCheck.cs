using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// What a query step's SQL must pass before it is stored — by a save from the query editor or by an AI actor creating or
/// refining a query: the shared execution gate in the data source's dialect, as the SQL will run (placeholders bound) —
/// regex guardrail, a single read-only statement, names in the source's own configured database only, no catalog, statistics
/// or query-history relation, the host policy for a host data source — and, when Beacon holds metadata for the source,
/// every column the SQL reads from a table in that metadata must exist there (a blocking catalog check; a table the
/// metadata does not list is left to the database). Every execution re-checks the read-only stages; this keeps SQL that
/// could never run, or that names objects the source does not have, from being stored at all. Registered with <c>TryAdd</c>.
/// </summary>
public interface IStepSqlSaveCheck
{
    /// <param name="placeholders">The step's parameter placeholders, bound the way execution binds them.</param>
    /// <returns>Null when the SQL may be stored; the reason it may not otherwise. A missing data source is a reason.</returns>
    Task<string?> RejectionAsync(int dataSourceId, string sql, IReadOnlyCollection<string> placeholders, CancellationToken cancellationToken);
}

/// <summary>
/// The built-in save check as the grounding SQL check uses it: the data source and its catalog are read through a
/// <see cref="StepSqlSaveCheck.SourceCache"/> the caller holds — one check, or a batch — so each is read once and the
/// grounding check takes the dialect from the same read the save check judges with. A host's own
/// <see cref="IStepSqlSaveCheck"/> does not implement it and is asked through its public method.
/// </summary>
internal interface IStepSqlSourceCheck
{
    /// <summary>The data source as the check reads it (null when it does not exist), read once per cache.</summary>
    Task<StepSqlSaveCheck.Target?> TargetAsync(int dataSourceId, StepSqlSaveCheck.SourceCache cache, CancellationToken cancellationToken);

    /// <summary>The data source's catalog (empty when Beacon holds no metadata for it), read once per cache.</summary>
    Task<IReadOnlyDictionary<string, HashSet<string>>> CatalogAsync(int dataSourceId, StepSqlSaveCheck.SourceCache cache, CancellationToken cancellationToken);

    /// <summary>As <see cref="IStepSqlSaveCheck.RejectionAsync"/>, reading the data source and its catalog through the cache.</summary>
    Task<string?> RejectionAsync(
        int dataSourceId,
        string sql,
        IReadOnlyCollection<string> placeholders,
        StepSqlSaveCheck.SourceCache cache,
        CancellationToken cancellationToken);
}

internal sealed class StepSqlSaveCheck(
    IDbContextFactory<BeaconContext> contextFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases) : IStepSqlSaveCheck, IStepSqlSourceCheck
{
    public Task<string?> RejectionAsync(int dataSourceId, string sql, IReadOnlyCollection<string> placeholders, CancellationToken cancellationToken)
    {
        return RejectionAsync(dataSourceId, sql, placeholders, new SourceCache(), cancellationToken);
    }

    /// <summary>
    /// As <see cref="RejectionAsync(int, string, IReadOnlyCollection{string}, CancellationToken)"/>; each data source and its
    /// catalog are read once for the <paramref name="cache"/>'s lifetime (one check, or a batch).
    /// </summary>
    public async Task<string?> RejectionAsync(
        int dataSourceId,
        string sql,
        IReadOnlyCollection<string> placeholders,
        SourceCache cache,
        CancellationToken cancellationToken)
    {
        var target = await TargetAsync(dataSourceId, cache, cancellationToken);
        if (target == null)
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
            StoredSql.Flatten(boundSql, target.Dialect),
            target.Dialect,
            EnforceReadOnly: true,
            DetectPii: false,
            CustomPiiPatterns: null,
            Catalog: null,
            BlockOnSchemaFailure: true,
            LintContext: null,
            EnableSemanticLint: false,
            MaxRows: null,
            // Judged before it is stored, never run here: row security applies wherever the step runs.
            RowSecurity: RowSecurityContext.NotApplicable(RowSecurityContext.SaveCheckReason))
        {
            HostManagedKey = target.HostManagedKey,
            ConfiguredDatabase = target.ConfiguredDatabase
        };

        // The read-only stages first: SQL that may never run is refused without reading the catalog.
        var readOnly = gate.Evaluate(request);
        if (readOnly.Blocked)
        {
            return readOnly.BlockReason ?? "The SQL was rejected.";
        }

        var catalog = await CatalogAsync(dataSourceId, cache, cancellationToken);

        // The catalog stage reads names, and on some dialects a bind marker reads as one (PostgreSQL parses @p0 as abs(p0)),
        // so it judges the statement with each placeholder written as NULL instead.
        var catalogSql = placeholders
            .Where(x => !string.IsNullOrEmpty(x))
            .Aggregate(sql, (current, placeholder) => current.Replace(placeholder, "NULL"));
        var report = gate.Evaluate(request with { Sql = StoredSql.Flatten(catalogSql, request.Dialect), Catalog = catalog });

        return report.Blocked ? report.BlockReason ?? "The SQL was rejected." : null;
    }

    public async Task<Target?> TargetAsync(int dataSourceId, SourceCache cache, CancellationToken cancellationToken)
    {
        if (!cache.Targets.TryGetValue(dataSourceId, out var target))
        {
            target = await ReadTargetAsync(dataSourceId, cancellationToken);
            cache.Targets.Add(dataSourceId, target);
        }

        return target;
    }

    public async Task<IReadOnlyDictionary<string, HashSet<string>>> CatalogAsync(int dataSourceId, SourceCache cache, CancellationToken cancellationToken)
    {
        if (!cache.Catalogs.TryGetValue(dataSourceId, out var catalog))
        {
            catalog = await ReadCatalogAsync(dataSourceId, cancellationToken);
            cache.Catalogs.Add(dataSourceId, catalog);
        }

        return catalog;
    }

    private async Task<Target?> ReadTargetAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken);

        return dataSource == null
            ? null
            : new Target(DataSourceSqlDialect.Of(dataSource), dataSource.HostManagedKey, configuredDatabases.Resolve(dataSource));
    }

    // Keyed as the MCP catalog is: the table alone and schema.table, lower case, each to its lower-case columns.
    private async Task<IReadOnlyDictionary<string, HashSet<string>>> ReadCatalogAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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

        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var columns = table.Columns
                .Select(x => x.ToLowerInvariant())
                .ToHashSet();
            catalog[table.TableName.ToLowerInvariant()] = columns;
            catalog[$"{table.SchemaName}.{table.TableName}".ToLowerInvariant()] = columns;
        }

        return catalog;
    }

    /// <summary>What the check reads of a data source to judge SQL for it.</summary>
    internal sealed record Target(string? Dialect, string? HostManagedKey, string? ConfiguredDatabase);

    /// <summary>The data sources and catalogs one check or one batch has read, by data source id (a missing data source as null).</summary>
    internal sealed class SourceCache
    {
        public Dictionary<int, Target?> Targets { get; } = [];

        public Dictionary<int, IReadOnlyDictionary<string, HashSet<string>>> Catalogs { get; } = [];
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
