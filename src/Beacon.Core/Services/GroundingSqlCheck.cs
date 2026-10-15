using System.Diagnostics.CodeAnalysis;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// What SQL must pass before Beacon stores it as grounding — SQL that later reaches a prompt as an example: the corrected SQL
/// of a feedback verdict, a golden case's gold SQL (promoted or edited) and a learned pattern's example SQL (mined or
/// approved). The SQL is read in its data source's dialect: its comments are removed, and SQL holding a comment that cannot
/// be removed safely is refused — comment characters inside a string literal are kept when the dialect's tokenizer reads
/// them as part of the literal (<see cref="GroundingSqlText"/>); SQL holding characters a reviewer cannot see
/// (<see cref="GroundingText"/>) is refused, and so is SQL for a data source Beacon holds no tables or columns for (there
/// is nothing to check it against). The comment-free SQL then passes <see cref="IStepSqlSaveCheck"/> (the execution
/// gate's read-only stages and the catalog check). Nothing here runs SQL. Registered with <c>TryAdd</c>.
/// </summary>
public interface IGroundingSqlCheck
{
    /// <returns>The comment-free SQL to store, or the reason it may not be stored. A missing data source is a reason.</returns>
    Task<GroundingSqlCheckResult> CheckAsync(int dataSourceId, string sql, CancellationToken cancellationToken);

    /// <summary>
    /// The same check for one batch of SQL texts — an aggregation run: each data source and its catalog are read once, on
    /// first use, and kept for the batch alone, so the next batch sees a metadata refresh. Not for concurrent use.
    /// </summary>
    IGroundingSqlCheck StartBatch();
}

/// <summary>The outcome of <see cref="IGroundingSqlCheck.CheckAsync"/>: the SQL to store, or why it may not be stored.</summary>
public sealed record GroundingSqlCheckResult
{
    private GroundingSqlCheckResult(string? sql, string? refusal, string? dialect)
    {
        Sql = sql;
        Refusal = refusal;
        Dialect = dialect;
    }

    /// <summary>The SQL to store, with its comments removed; null when refused.</summary>
    public string? Sql { get; }

    /// <summary>Why the SQL may not be stored; null when it may.</summary>
    public string? Refusal { get; }

    /// <summary>
    /// The dialect the SQL was read in — the data source's — when it was allowed; null when refused or not known. A caller
    /// compares stored grounding SQL with <see cref="Sql"/> in it (<see cref="GroundingSqlText.WithoutComments(string, string?)"/>).
    /// </summary>
    public string? Dialect { get; }

    [MemberNotNullWhen(true, nameof(Sql))]
    [MemberNotNullWhen(false, nameof(Refusal))]
    public bool IsAllowed => Refusal == null;

    public static GroundingSqlCheckResult Allowed(string sql, string? dialect = null)
    {
        return new GroundingSqlCheckResult(sql, null, dialect);
    }

    public static GroundingSqlCheckResult Refused(string reason)
    {
        return new GroundingSqlCheckResult(null, reason, null);
    }
}

internal sealed class GroundingSqlCheck(
    IDbContextFactory<BeaconContext> contextFactory,
    IStepSqlSaveCheck saveCheck) : IGroundingSqlCheck
{
    internal const string CommentRefusal =
        "The SQL holds a comment that cannot be removed safely. Remove the comment and try again; comment characters inside a "
        + "string literal are allowed when Beacon reads the literal as the database does.";

    internal const string HiddenCharacterRefusal =
        "The SQL holds characters that do not show on screen (such as bidirectional overrides, zero-width characters, tag "
        + "characters or variation selectors). Remove them and try again.";

    internal const string NoCatalogRefusal =
        "Beacon holds no tables or columns for this data source, so the SQL cannot be checked against it. Extract metadata for "
        + "this data source first.";

    public Task<GroundingSqlCheckResult> CheckAsync(int dataSourceId, string sql, CancellationToken cancellationToken)
    {
        return CheckAsync(dataSourceId, sql, new BatchSources(), cancellationToken);
    }

    public IGroundingSqlCheck StartBatch()
    {
        return new Batch(this);
    }

    private async Task<GroundingSqlCheckResult> CheckAsync(int dataSourceId, string sql, BatchSources sources, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return GroundingSqlCheckResult.Refused("The SQL is empty.");
        }

        if (GroundingText.HasHiddenCharacters(sql))
        {
            return GroundingSqlCheckResult.Refused(HiddenCharacterRefusal);
        }

        return saveCheck is IStepSqlSourceCheck sourceCheck
            ? await CheckWithSourcesAsync(sourceCheck, dataSourceId, sql, sources.Steps, cancellationToken)
            : await CheckWithHostCheckAsync(dataSourceId, sql, sources, cancellationToken);
    }

    // The built-in save check: the data source and its catalog are read once, through the cache it judges with as well.
    private static async Task<GroundingSqlCheckResult> CheckWithSourcesAsync(
        IStepSqlSourceCheck sourceCheck,
        int dataSourceId,
        string sql,
        StepSqlSaveCheck.SourceCache cache,
        CancellationToken cancellationToken)
    {
        var target = await sourceCheck.TargetAsync(dataSourceId, cache, cancellationToken);
        if (target == null)
        {
            return GroundingSqlCheckResult.Refused($"Data source {dataSourceId} not found.");
        }

        var commentFree = GroundingSqlText.WithoutComments(sql, target.Dialect);
        if (commentFree == null)
        {
            return GroundingSqlCheckResult.Refused(CommentRefusal);
        }

        var rejection = await sourceCheck.RejectionAsync(dataSourceId, commentFree, [], cache, cancellationToken);
        if (rejection != null)
        {
            return GroundingSqlCheckResult.Refused(rejection);
        }

        var catalog = await sourceCheck.CatalogAsync(dataSourceId, cache, cancellationToken);

        return catalog.Count == 0
            ? GroundingSqlCheckResult.Refused(NoCatalogRefusal)
            : GroundingSqlCheckResult.Allowed(commentFree, target.Dialect);
    }

    // A host's own save check: the dialect and whether a catalog exists are read here (one read per data source and cache),
    // and the host's check is asked for every SQL.
    private async Task<GroundingSqlCheckResult> CheckWithHostCheckAsync(
        int dataSourceId,
        string sql,
        BatchSources sources,
        CancellationToken cancellationToken)
    {
        if (!sources.Sources.TryGetValue(dataSourceId, out var source))
        {
            source = await SourceAsync(dataSourceId, cancellationToken);
            sources.Sources.Add(dataSourceId, source);
        }

        if (source == null)
        {
            return GroundingSqlCheckResult.Refused($"Data source {dataSourceId} not found.");
        }

        var commentFree = GroundingSqlText.WithoutComments(sql, source.Dialect);
        if (commentFree == null)
        {
            return GroundingSqlCheckResult.Refused(CommentRefusal);
        }

        var rejection = await saveCheck.RejectionAsync(dataSourceId, commentFree, [], cancellationToken);
        if (rejection != null)
        {
            return GroundingSqlCheckResult.Refused(rejection);
        }

        return source.HasCatalog
            ? GroundingSqlCheckResult.Allowed(commentFree, source.Dialect)
            : GroundingSqlCheckResult.Refused(NoCatalogRefusal);
    }

    private async Task<SourceDialect?> SourceAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var source = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .Select(x =>
                new
                {
                    x.DataSourceType,
                    x.DatabaseEngineType,
                    HasCatalog = context.DatabaseMetadata.Any(y => y.DataSourceId == x.Id)
                })
            .FirstOrDefaultAsync(cancellationToken);

        return source == null
            ? null
            : new SourceDialect(DataSourceSqlDialect.Of(source.DataSourceType, source.DatabaseEngineType), source.HasCatalog);
    }

    private sealed record SourceDialect(string? Dialect, bool HasCatalog);

    // What one check or one batch has read, by data source id: a missing data source is remembered as null.
    private sealed class BatchSources
    {
        public Dictionary<int, SourceDialect?> Sources { get; } = [];

        public StepSqlSaveCheck.SourceCache Steps { get; } = new();
    }

    private sealed class Batch(GroundingSqlCheck owner) : IGroundingSqlCheck
    {
        private readonly BatchSources _sources = new();

        public Task<GroundingSqlCheckResult> CheckAsync(int dataSourceId, string sql, CancellationToken cancellationToken)
        {
            return owner.CheckAsync(dataSourceId, sql, _sources, cancellationToken);
        }

        public IGroundingSqlCheck StartBatch()
        {
            return this;
        }
    }
}

/// <summary>
/// Grounding SQL as a prompt shows it: without its comments, read in the data source's dialect. SQL stored before Beacon
/// checked grounding SQL may still carry comments; rendering it through here removes them without changing what is stored.
/// </summary>
public static class GroundingSqlText
{
    /// <returns>
    /// The SQL without its comments, trimmed; null when it must be left out: a comment cannot be removed safely (an unknown
    /// dialect, SQL the tokenizer cannot read, an executable comment), a comment marker is left that the dialect's tokenizer
    /// does not read as part of a string literal, or the SQL holds characters a reviewer cannot see (<see cref="GroundingText"/>).
    /// </returns>
    public static string? WithoutComments(string sql, DataSourceType dataSourceType, DatabaseEngineType? engineType)
    {
        return WithoutComments(sql, DataSourceSqlDialect.Of(dataSourceType, engineType));
    }

    /// <summary>As <see cref="WithoutComments(string, DataSourceType, DatabaseEngineType?)"/>, for a dialect name.</summary>
    public static string? WithoutComments(string sql, string? dialect)
    {
        if (GroundingText.HasHiddenCharacters(sql))
        {
            return null;
        }

        // The stripper leaves the text as written wherever it cannot remove a comment safely, so a comment marker left over
        // is either text inside a string literal — kept when the tokenizer places it there — or a comment that stays.
        var commentFree = SqlCommentStripper.Strip(sql, dialect).Trim();
        if (!commentFree.Contains("/*", StringComparison.Ordinal) && !SqlDialects.HasLineCommentMarker(commentFree, dialect))
        {
            return commentFree;
        }

        return SqlCommentStripper.MarkersOnlyInLiterals(commentFree, dialect) ? commentFree : null;
    }

    /// <summary>
    /// Grounding SQL in the form two versions of it are compared in: without its comments
    /// (<see cref="WithoutComments(string, string?)"/>), with each run of whitespace between the dialect's tokens made one
    /// space — string literals, quoted identifiers and every other token exactly as written, so a changed number, literal or
    /// identifier (its case included) is a change. Null when it cannot be read so: compare it as stored.
    /// </summary>
    public static string? ForComparison(string sql, string? dialect)
    {
        var commentFree = WithoutComments(sql, dialect);

        return commentFree == null ? null : SqlCommentStripper.WithWhitespaceCollapsed(commentFree, dialect);
    }
}
