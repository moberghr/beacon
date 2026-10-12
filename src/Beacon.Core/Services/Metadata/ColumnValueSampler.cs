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
    /// A column whose name <see cref="ColumnValueExclusions"/> leaves out (PII, secret-like, a person's name, a financial
    /// amount), or a binary column, is not read at all (the row sample names the other columns, a table with none is not
    /// queried); one of whose values looks like PII or a credential — screened whole, before a sample is cut to
    /// <see cref="MaxValueLength"/> — is returned with no samples, so the refresh that stores the result clears any it held
    /// before. Each refresh logs how many columns it left out by name, for a PII-shaped value and for a credential-shaped
    /// value (counts only).
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
        var tally = new ExclusionTally();
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
                var (samples, completeDomainColumns, valueExclusions) = await SampleTableAsync(dataSource, engineType, table, customPiiPatterns, cancellationToken);
                enriched.Add(table with { Columns = ApplySamples(table.Columns, samples, customPiiPatterns, completeDomainColumns, valueExclusions, tally) });
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
            "Sample-value collection of data source {DataSourceId}: {SampledCount} of {TableCount} tables sampled; columns left out by name {ByName}, for a PII-shaped value {ByPiiValue}, for a credential-shaped value {ByCredentialValue}",
            dataSource.Id,
            sampled,
            candidates,
            tally.ByName,
            tally.ByPiiValue,
            tally.ByCredentialValue);

        return enriched;
    }

    private async Task<(Dictionary<string, List<string>> Samples, HashSet<string> CompleteDomainColumns, Dictionary<string, SampleExclusion> ValueExclusions)> SampleTableAsync(
        DataSource dataSource,
        DatabaseEngineType engineType,
        TableMetadataDto table,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken)
    {
        // Only the columns whose values may describe them are read: a binary column, or one whose name is left out (PII,
        // secret-like, a person's name, a financial amount), never leaves the database.
        var readable = table.Columns
            .Where(x => !ShouldSkipColumn(x, customPiiPatterns))
            .Select(x => x.ColumnName)
            .ToList();
        var rows = readable.Count == 0
            ? []
            : await ReadAsync(dataSource, BuildSampleQuery(engineType, table.SchemaName, table.TableName, readable), SampleRowCount, cancellationToken);
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var valueExclusions = new Dictionary<string, SampleExclusion>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var kvp in row)
            {
                // Every value read is screened whole — a credential or IBAN past the sample length still counts.
                var fullText = FullText(kvp.Value);
                if (fullText == null)
                {
                    continue;
                }

                NoteExclusion(valueExclusions, kvp.Key, ScreenValue(fullText, customPiiPatterns));

                if (!samples.TryGetValue(kvp.Key, out var values))
                {
                    values = [];
                    samples[kvp.Key] = values;
                }

                var text = Cut(fullText);
                if (values.Count < IColumnValueSampler.MaxValuesPerColumn && !values.Contains(text, StringComparer.Ordinal))
                {
                    values.Add(text);
                }
            }
        }

        var completeDomainColumns = await ProbeTableDomainsAsync(dataSource, engineType, table, samples, valueExclusions, customPiiPatterns, cancellationToken);

        return (samples, completeDomainColumns, valueExclusions);
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
        Dictionary<string, SampleExclusion> valueExclusions,
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
                var fullValues = await ProbeDomainAsync(dataSource, engineType, table, column, cancellationToken);

                // A value the probe read that looks like PII or a credential leaves the whole column out.
                foreach (var value in fullValues)
                {
                    NoteExclusion(valueExclusions, column.ColumnName, ScreenValue(value, customPiiPatterns));
                }

                if (!IsCompleteDomain(fullValues) || valueExclusions.ContainsKey(column.ColumnName))
                {
                    continue;
                }

                samples[column.ColumnName] = fullValues
                    .Select(Cut)
                    .ToList();
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
            var text = FullText(row.Values.FirstOrDefault());
            if (text != null)
            {
                values.Add(text);
            }
        }

        return values;
    }

    /// <param name="valueExclusions">Columns one of whose values, screened whole as read, looked like PII or a credential.</param>
    /// <param name="tally">Counts the columns left out, by reason, for the refresh's log.</param>
    internal IReadOnlyList<ColumnMetadataDto> ApplySamples(
        IReadOnlyList<ColumnMetadataDto> columns,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns,
        IReadOnlySet<string>? completeDomainColumns = null,
        IReadOnlyDictionary<string, SampleExclusion>? valueExclusions = null,
        ExclusionTally? tally = null)
    {
        return columns
            .Select(x => ApplySampleToColumn(x, samples, customPiiPatterns, completeDomainColumns, valueExclusions, tally))
            .ToList();
    }

    private ColumnMetadataDto ApplySampleToColumn(
        ColumnMetadataDto column,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns,
        IReadOnlySet<string>? completeDomainColumns,
        IReadOnlyDictionary<string, SampleExclusion>? valueExclusions,
        ExclusionTally? tally)
    {
        if (BinaryDataTypes.Contains(NormalizeDataType(column.DataType)))
        {
            return WithoutSamples(column);
        }

        if (ColumnValueExclusions.IsExcludedColumn(guardrailService, column.ColumnName, customPiiPatterns))
        {
            tally?.Count(SampleExclusion.Name);
            return WithoutSamples(column);
        }

        if (!samples.TryGetValue(column.ColumnName, out var values) || values.Count == 0)
        {
            return WithoutSamples(column);
        }

        var exclusion = valueExclusions != null && valueExclusions.TryGetValue(column.ColumnName, out var screened)
            ? screened
            : ScreenValues(values, customPiiPatterns);
        if (exclusion != SampleExclusion.None)
        {
            tally?.Count(exclusion);
            return WithoutSamples(column);
        }

        var isComplete = completeDomainColumns != null && completeDomainColumns.Contains(column.ColumnName);
        return column with { SampleValues = values, SampleValuesComplete = isComplete };
    }

    internal static bool ContainsExcludedValue(IReadOnlyList<string> values, IReadOnlyList<string>? customPiiPatterns)
    {
        return ColumnValueExclusions.ContainsExcludedValue(values, customPiiPatterns);
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

        return ColumnValueExclusions.IsExcludedColumn(guardrailService, column.ColumnName, customPiiPatterns);
    }

    // A column left unsampled carries no samples, whatever the extracted metadata held: the refresh that stores the result
    // clears what the column held before.
    private static ColumnMetadataDto WithoutSamples(ColumnMetadataDto column)
    {
        return column.SampleValues == null && !column.SampleValuesComplete
            ? column
            : column with { SampleValues = null, SampleValuesComplete = false };
    }

    /// <summary>
    /// The row sample of a table: the named columns only, each name validated and quoted in the engine's own way, as the
    /// schema and table are.
    /// </summary>
    internal static string BuildSampleQuery(DatabaseEngineType engineType, string schemaName, string tableName, IReadOnlyList<string> columnNames)
    {
        var schema = SqlIdentifierGuard.Validate(schemaName, "schema");
        var table = SqlIdentifierGuard.Validate(tableName, "table");
        var columns = columnNames
            .Select(x => SqlIdentifierGuard.Validate(x, "column"))
            .ToList();
        return engineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse =>
                $"SELECT TOP {SampleRowCount} {QuotedList(columns, '[', ']')} FROM [{SqlIdentifierGuard.EscapeQuote(schema, ']')}].[{SqlIdentifierGuard.EscapeQuote(table, ']')}]",
            DatabaseEngineType.MySQL =>
                $"SELECT {QuotedList(columns, '`', '`')} FROM `{SqlIdentifierGuard.EscapeQuote(schema, '`')}`.`{SqlIdentifierGuard.EscapeQuote(table, '`')}` LIMIT {SampleRowCount}",
            _ =>
                $"SELECT {QuotedList(columns, '"', '"')} FROM \"{SqlIdentifierGuard.EscapeQuote(schema, '"')}\".\"{SqlIdentifierGuard.EscapeQuote(table, '"')}\" LIMIT {SampleRowCount}"
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

    private static string QuotedList(IReadOnlyList<string> columns, char open, char close)
    {
        return string.Join(", ", columns.Select(x => $"{open}{SqlIdentifierGuard.EscapeQuote(x, close)}{close}"));
    }

    internal static string? FormatValue(object value)
    {
        var text = FullText(value);

        return text == null ? null : Cut(text);
    }

    // A value as text, whole; null for a null, binary or blank value.
    private static string? FullText(object? value)
    {
        if (value is null or byte[])
        {
            return null;
        }

        var text = value switch
        {
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Cut(string text)
    {
        return text.Length > IColumnValueSampler.MaxValueLength
            ? text[..IColumnValueSampler.MaxValueLength]
            : text;
    }

    private static SampleExclusion ScreenValue(string value, IReadOnlyList<string>? customPiiPatterns)
    {
        if (PiiValueScreen.ContainsPiiValue([value], customPiiPatterns))
        {
            return SampleExclusion.PiiValue;
        }

        return CredentialValueScreen.LooksLikeCredential(value) ? SampleExclusion.CredentialValue : SampleExclusion.None;
    }

    private static SampleExclusion ScreenValues(IReadOnlyList<string> values, IReadOnlyList<string>? customPiiPatterns)
    {
        return values
            .Select(x => ScreenValue(x, customPiiPatterns))
            .FirstOrDefault(x => x != SampleExclusion.None);
    }

    // The first reason found for a column is the one it is counted under.
    private static void NoteExclusion(Dictionary<string, SampleExclusion> valueExclusions, string column, SampleExclusion exclusion)
    {
        if (exclusion != SampleExclusion.None)
        {
            valueExclusions.TryAdd(column, exclusion);
        }
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

/// <summary>Why a column's sample values were left out.</summary>
internal enum SampleExclusion
{
    None,

    /// <summary>Its name: PII, secret-like, a person's name or a financial amount (<see cref="ColumnValueExclusions"/>).</summary>
    Name,

    /// <summary>A value read from it looked like PII (<see cref="PiiValueScreen"/>).</summary>
    PiiValue,

    /// <summary>A value read from it looked like a credential (<see cref="CredentialValueScreen"/>).</summary>
    CredentialValue
}

/// <summary>The columns one refresh left out, by reason — counts only, for its log.</summary>
internal sealed class ExclusionTally
{
    public int ByName { get; private set; }

    public int ByPiiValue { get; private set; }

    public int ByCredentialValue { get; private set; }

    public void Count(SampleExclusion exclusion)
    {
        switch (exclusion)
        {
            case SampleExclusion.Name:
                ByName++;
                break;
            case SampleExclusion.PiiValue:
                ByPiiValue++;
                break;
            case SampleExclusion.CredentialValue:
                ByCredentialValue++;
                break;
        }
    }
}
