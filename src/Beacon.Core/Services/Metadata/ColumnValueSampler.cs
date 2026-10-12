using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Services.Metadata;

public interface IColumnValueSampler
{
    /// <summary>
    /// Samples up to <see cref="MaxValuesPerColumn"/> representative values per column for each table
    /// (one query per table) and returns the tables with enriched <c>SampleValues</c>. Also probes up
    /// to 8 short string columns per table for their complete value domain (bounded DISTINCT); a domain
    /// of <see cref="MaxCompleteDomainValues"/> or fewer values replaces the sample and is marked
    /// <c>SampleValuesComplete</c>. Every sampling statement passes the shared execution gate and runs through the
    /// provider's read-only path (<see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>) on the data source's own
    /// connection, with a row cap and a statement timeout. Sampling failures are logged and never fail the metadata refresh.
    /// </summary>
    Task<IReadOnlyList<TableMetadataDto>> EnrichWithSampleValuesAsync(
        DataSource dataSource,
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken = default);

    const int MaxValuesPerColumn = 5;
    const int MaxValueLength = 50;
    const int MaxCompleteDomainValues = 12;
}

internal sealed class ColumnValueSampler(
    IQueryGuardrailService guardrailService,
    GatedSqlRunner sqlRunner,
    ILogger<ColumnValueSampler> logger) : IColumnValueSampler
{
    private const int SampleRowCount = 5;
    private static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);
    private const int MaxCandidateColumnsPerTable = 8;
    private const int DomainProbeInnerScanRowCount = 1000;
    private const int MaxDomainCandidateLength = 100;

    // Why a table or column was not sampled, as the log names it — never the exception's message.
    private const string RefusedByTheGate = "the execution gate refused the sampling statement";
    private const string FailedOnTheDataSource = "the sampling statement failed on the data source";
    private const string TimedOut = "the sampling statement timed out";
    private const string NotAPlainIdentifier = "a name is not a plain identifier";

    private static readonly HashSet<string> BinaryDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bytea", "blob", "binary", "varbinary", "image", "longblob", "mediumblob", "tinyblob"
    };

    // Type rule for value-domain candidates (§ complete value domains): short string types only.
    private static readonly string[] DomainCandidateTypeFragments =
    [
        "char", "varchar", "nvarchar", "nchar", "text", "character varying", "enum", "name", "bpchar", "string"
    ];

    public async Task<IReadOnlyList<TableMetadataDto>> EnrichWithSampleValuesAsync(
        DataSource dataSource,
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken = default)
    {
        var enriched = new List<TableMetadataDto>(tables.Count);
        if (dataSource.DatabaseEngineType is not { } engineType)
        {
            return tables;
        }

        var candidates = 0;
        var sampled = 0;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (table.Columns.Count == 0)
            {
                enriched.Add(table);
                continue;
            }

            candidates++;
            try
            {
                var (samples, completeDomainColumns) = await SampleTableAsync(dataSource, engineType, table, customPiiPatterns, cancellationToken);
                enriched.Add(table with { Columns = ApplySamples(table.Columns, samples, customPiiPatterns, completeDomainColumns) });
                sampled++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The table and why, never the exception's message, which can quote row values (§1.11).
                logger.LogWarning(
                    "Sample-value collection skipped table {Table} of data source {DataSourceId} ({Reason}); its samples are left empty",
                    SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}"),
                    dataSource.Id,
                    ReasonOf(ex));
                enriched.Add(table);
            }
        }

        logger.LogInformation(
            "Sample-value collection of data source {DataSourceId}: {SampledCount} of {TableCount} tables sampled",
            dataSource.Id,
            sampled,
            candidates);

        return enriched;
    }

    private async Task<(Dictionary<string, List<string>> Samples, HashSet<string> CompleteDomainColumns)> SampleTableAsync(
        DataSource dataSource,
        DatabaseEngineType engineType,
        TableMetadataDto table,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken)
    {
        var sql = BuildSampleQuery(engineType, table.SchemaName, table.TableName);
        var rows = await ReadAsync(dataSource, sql, SampleRowCount, cancellationToken);
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var kvp in row)
            {
                if (kvp.Value == null)
                {
                    continue;
                }

                if (!samples.TryGetValue(kvp.Key, out var values))
                {
                    values = [];
                    samples[kvp.Key] = values;
                }

                if (values.Count >= IColumnValueSampler.MaxValuesPerColumn)
                {
                    continue;
                }

                var text = FormatValue(kvp.Value);
                if (text != null && !values.Contains(text, StringComparer.Ordinal))
                {
                    values.Add(text);
                }
            }
        }

        var completeDomainColumns = await ProbeTableDomainsAsync(dataSource, engineType, table, samples, customPiiPatterns, cancellationToken);

        return (samples, completeDomainColumns);
    }

    // Runs one sampling statement through GatedSqlRunner: the shared execution gate in the source's dialect (the names are
    // quoted catalog identifiers, but the gate also holds them to the source's own configured database and the host
    // policy), then the provider's read-only path (a READ ONLY transaction where the engine has one), capped one row past
    // the statement's own bound — so the read ends on its own — and within the sampler's statement timeout. A failure's
    // message can quote row values, so it is not passed on (§1.11); the provider has logged its type and error code. Each
    // failure is thrown with the class of its reason, which the caller logs.
    private async Task<List<Dictionary<string, object?>>> ReadAsync(
        DataSource dataSource,
        string sql,
        int statementBound,
        CancellationToken cancellationToken)
    {
        GatedSqlRun run;
        try
        {
            run = await sqlRunner.RunAsync(
                new GatedSqlRequest(dataSource, sql, new Dictionary<string, object?>(), statementBound + 1, Timeout: StatementTimeout),
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new SamplingSkippedException(RefusedByTheGate);
        }
        catch (QueryExecutionFailedException)
        {
            throw new SamplingSkippedException(FailedOnTheDataSource);
        }

        return run.TimedOut
            ? throw new SamplingSkippedException(TimedOut)
            : run.Rows;
    }

    private async Task<HashSet<string>> ProbeTableDomainsAsync(
        DataSource dataSource,
        DatabaseEngineType engineType,
        TableMetadataDto table,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken)
    {
        var completeDomainColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = SelectDomainCandidates(table.Columns, customPiiPatterns);

        foreach (var column in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var values = await ProbeDomainAsync(dataSource, engineType, table, column, cancellationToken);
                if (!IsCompleteDomain(values))
                {
                    continue;
                }

                if (ContainsPiiValue(values, customPiiPatterns))
                {
                    continue;
                }

                samples[column.ColumnName] = values;
                completeDomainColumns.Add(column.ColumnName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Domain probe skipped column {Column} of data source {DataSourceId} ({Reason}); keeping its row sample",
                    SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}.{column.ColumnName}"),
                    dataSource.Id,
                    ReasonOf(ex));
            }
        }

        return completeDomainColumns;
    }

    /// <summary>
    /// TEST-3: the count→complete decision the bounded DISTINCT domain probe relies on, extracted so it
    /// is directly unit-testable without a database. The probe is capped one above
    /// <see cref="IColumnValueSampler.MaxCompleteDomainValues"/> (spec item 4), so returning that many
    /// rows back means the true domain is larger than the cap — NOT complete. Zero rows back is treated
    /// the same as "not complete" (nothing to persist; the existing row sample is kept).
    /// </summary>
    internal static bool IsCompleteDomain(IReadOnlyList<string> values)
    {
        return values.Count > 0 && values.Count <= IColumnValueSampler.MaxCompleteDomainValues;
    }

    private async Task<List<string>> ProbeDomainAsync(
        DataSource dataSource,
        DatabaseEngineType engineType,
        TableMetadataDto table,
        ColumnMetadataDto column,
        CancellationToken cancellationToken)
    {
        var sql = BuildDistinctProbeQuery(engineType, table.SchemaName, table.TableName, column.ColumnName);
        var rows = await ReadAsync(dataSource, sql, IColumnValueSampler.MaxCompleteDomainValues + 1, cancellationToken);
        var values = new List<string>();

        foreach (var row in rows)
        {
            var value = row.Values.FirstOrDefault();
            if (value == null)
            {
                continue;
            }

            var text = FormatValue(value);
            if (text != null)
            {
                values.Add(text);
            }
        }

        return values;
    }

    internal IReadOnlyList<ColumnMetadataDto> ApplySamples(
        IReadOnlyList<ColumnMetadataDto> columns,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns,
        IReadOnlySet<string>? completeDomainColumns = null)
    {
        return columns
            .Select(x => ApplySampleToColumn(x, samples, customPiiPatterns, completeDomainColumns))
            .ToList();
    }

    private ColumnMetadataDto ApplySampleToColumn(
        ColumnMetadataDto column,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns,
        IReadOnlySet<string>? completeDomainColumns)
    {
        if (ShouldSkipColumn(column, customPiiPatterns))
        {
            return column;
        }

        if (!samples.TryGetValue(column.ColumnName, out var values) || values.Count == 0)
        {
            return column;
        }

        if (ContainsPiiValue(values, customPiiPatterns))
        {
            return column;
        }

        var isComplete = completeDomainColumns != null && completeDomainColumns.Contains(column.ColumnName);
        return column with { SampleValues = values, SampleValuesComplete = isComplete };
    }

    internal static bool ContainsPiiValue(IReadOnlyList<string> values, IReadOnlyList<string>? customPiiPatterns)
    {
        return PiiValueScreen.ContainsPiiValue(values, customPiiPatterns);
    }

    internal bool IsDomainCandidate(ColumnMetadataDto column, IReadOnlyList<string>? customPiiPatterns)
    {
        if (column.IsPrimaryKey || column.IsForeignKey)
        {
            return false;
        }

        if (column.MaxLength is > MaxDomainCandidateLength)
        {
            return false;
        }

        var normalizedType = NormalizeDataType(column.DataType);
        var isCandidateType = DomainCandidateTypeFragments
            .Any(x => normalizedType.Contains(x, StringComparison.OrdinalIgnoreCase));

        if (!isCandidateType)
        {
            return false;
        }

        return !ShouldSkipColumn(column, customPiiPatterns);
    }

    internal IReadOnlyList<ColumnMetadataDto> SelectDomainCandidates(
        IReadOnlyList<ColumnMetadataDto> columns,
        IReadOnlyList<string>? customPiiPatterns)
    {
        return columns
            .Where(x => IsDomainCandidate(x, customPiiPatterns))
            .Take(MaxCandidateColumnsPerTable)
            .ToList();
    }

    private bool ShouldSkipColumn(ColumnMetadataDto column, IReadOnlyList<string>? customPiiPatterns)
    {
        if (BinaryDataTypes.Contains(NormalizeDataType(column.DataType)))
        {
            return true;
        }

        return guardrailService.IsPiiColumn(column.ColumnName, customPiiPatterns);
    }

    internal static string BuildSampleQuery(DatabaseEngineType engineType, string schemaName, string tableName)
    {
        var schema = SqlIdentifierGuard.Validate(schemaName, "schema");
        var table = SqlIdentifierGuard.Validate(tableName, "table");
        return engineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse =>
                $"SELECT TOP {SampleRowCount} * FROM [{SqlIdentifierGuard.EscapeQuote(schema, ']')}].[{SqlIdentifierGuard.EscapeQuote(table, ']')}]",
            DatabaseEngineType.MySQL =>
                $"SELECT * FROM `{SqlIdentifierGuard.EscapeQuote(schema, '`')}`.`{SqlIdentifierGuard.EscapeQuote(table, '`')}` LIMIT {SampleRowCount}",
            _ =>
                $"SELECT * FROM \"{SqlIdentifierGuard.EscapeQuote(schema, '"')}\".\"{SqlIdentifierGuard.EscapeQuote(table, '"')}\" LIMIT {SampleRowCount}"
        };
    }

    /// <summary>
    /// Bounded DISTINCT probe for a candidate column's full value domain: an inner scan of at most
    /// <see cref="DomainProbeInnerScanRowCount"/> rows, then DISTINCT capped one above
    /// <see cref="IColumnValueSampler.MaxCompleteDomainValues"/> so the caller can tell "complete"
    /// (≤ 12 rows back) from "more values exist" (13 rows back) with a single round trip.
    /// </summary>
    internal static string BuildDistinctProbeQuery(DatabaseEngineType engineType, string schemaName, string tableName, string columnName)
    {
        var schema = SqlIdentifierGuard.Validate(schemaName, "schema");
        var table = SqlIdentifierGuard.Validate(tableName, "table");
        var column = SqlIdentifierGuard.Validate(columnName, "column");
        var probeLimit = IColumnValueSampler.MaxCompleteDomainValues + 1;

        if (engineType is DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse)
        {
            var bracketColumn = SqlIdentifierGuard.EscapeQuote(column, ']');
            var bracketSchema = SqlIdentifierGuard.EscapeQuote(schema, ']');
            var bracketTable = SqlIdentifierGuard.EscapeQuote(table, ']');
            return $"SELECT DISTINCT TOP {probeLimit} [{bracketColumn}] FROM (SELECT TOP {DomainProbeInnerScanRowCount} [{bracketColumn}] FROM [{bracketSchema}].[{bracketTable}] WHERE [{bracketColumn}] IS NOT NULL) x";
        }

        // MySQL only treats "..." as an identifier under ANSI_QUOTES; default sql_mode reads it as a
        // string literal, so quote with backticks exactly like BuildSampleQuery does.
        var quoteChar = engineType == DatabaseEngineType.MySQL ? '`' : '"';
        var quotedColumn = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(column, quoteChar)}{quoteChar}";
        var quotedSchema = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(schema, quoteChar)}{quoteChar}";
        var quotedTable = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(table, quoteChar)}{quoteChar}";
        return $"SELECT DISTINCT {quotedColumn} FROM (SELECT {quotedColumn} FROM {quotedSchema}.{quotedTable} WHERE {quotedColumn} IS NOT NULL LIMIT {DomainProbeInnerScanRowCount}) x LIMIT {probeLimit}";
    }

    internal static string? FormatValue(object value)
    {
        if (value is byte[])
        {
            return null;
        }

        var text = value switch
        {
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length > IColumnValueSampler.MaxValueLength
            ? text[..IColumnValueSampler.MaxValueLength]
            : text;
    }

    private static string NormalizeDataType(string dataType)
    {
        var parenIndex = dataType.IndexOf('(');
        return (parenIndex > 0 ? dataType[..parenIndex] : dataType).Trim();
    }

    // The class of a sampling failure for the log: the reason a statement was not run or did not finish, or the exception
    // type for anything else.
    private static string ReasonOf(Exception exception)
    {
        return exception switch
        {
            SamplingSkippedException skipped => skipped.Message,
            InvalidOperationException => NotAPlainIdentifier,
            _ => exception.GetType().Name
        };
    }

    // A sampling statement that was not run or did not finish, with the class of its reason as the message.
    private sealed class SamplingSkippedException(string reason) : Exception(reason);
}
