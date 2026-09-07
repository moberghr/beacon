using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Services.Metadata;

public interface IColumnValueSampler
{
    /// <summary>
    /// Samples up to <see cref="MaxValuesPerColumn"/> representative values per column for each table
    /// (one query per table) and returns the tables with enriched <c>SampleValues</c>. Also probes up
    /// to 8 short string columns per table for their complete value domain (bounded DISTINCT); a domain
    /// of <see cref="MaxCompleteDomainValues"/> or fewer values replaces the sample and is marked
    /// <c>SampleValuesComplete</c>. Sampling failures are logged and never fail the metadata refresh.
    /// </summary>
    Task<IReadOnlyList<TableMetadataDto>> EnrichWithSampleValuesAsync(
        DatabaseEngineType engineType,
        string connectionString,
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken = default);

    const int MaxValuesPerColumn = 5;
    const int MaxValueLength = 50;
    const int MaxCompleteDomainValues = 12;
}

internal sealed class ColumnValueSampler(
    IQueryGuardrailService guardrailService,
    ILogger<ColumnValueSampler> logger) : IColumnValueSampler
{
    private const int SampleRowCount = 5;
    private const int CommandTimeoutSeconds = 30;
    private const int MaxCandidateColumnsPerTable = 8;
    private const int DomainProbeInnerScanRowCount = 1000;
    private const int MaxDomainCandidateLength = 100;

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
        DatabaseEngineType engineType,
        string connectionString,
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken = default)
    {
        var enriched = new List<TableMetadataDto>(tables.Count);

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (table.Columns.Count == 0)
            {
                enriched.Add(table);
                continue;
            }

            try
            {
                var (samples, completeDomainColumns) = await SampleTableAsync(engineType, connectionString, table, customPiiPatterns, cancellationToken);
                enriched.Add(table with { Columns = ApplySamples(table.Columns, samples, customPiiPatterns, completeDomainColumns) });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sample-value collection failed for table {Schema}.{Table}; leaving samples empty",
                    table.SchemaName, table.TableName);
                enriched.Add(table);
            }
        }

        return enriched;
    }

    private async Task<(Dictionary<string, List<string>> Samples, HashSet<string> CompleteDomainColumns)> SampleTableAsync(
        DatabaseEngineType engineType,
        string connectionString,
        TableMetadataDto table,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken)
    {
        var sql = BuildSampleQuery(engineType, table.SchemaName, table.TableName);

        await using var connection = DbConnectionFactory.CreateConnection(engineType, connectionString);
        await connection.OpenAsync(cancellationToken);

        var commandDefinition = new CommandDefinition(
            sql,
            cancellationToken: cancellationToken,
            commandTimeout: CommandTimeoutSeconds);

        var rows = (await connection.QueryAsync(commandDefinition)).AsList();
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object> rowDict)
            {
                continue;
            }

            foreach (var kvp in rowDict)
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

        var completeDomainColumns = await ProbeTableDomainsAsync(connection, engineType, table, samples, customPiiPatterns, cancellationToken);

        return (samples, completeDomainColumns);
    }

    private async Task<HashSet<string>> ProbeTableDomainsAsync(
        IDbConnection connection,
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
                var values = await ProbeDomainAsync(connection, engineType, table, column, cancellationToken);
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
                logger.LogWarning(ex, "Domain probe failed for column {Schema}.{Table}.{Column}; keeping row sample",
                    table.SchemaName, table.TableName, column.ColumnName);
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

    private static async Task<List<string>> ProbeDomainAsync(
        IDbConnection connection,
        DatabaseEngineType engineType,
        TableMetadataDto table,
        ColumnMetadataDto column,
        CancellationToken cancellationToken)
    {
        var sql = BuildDistinctProbeQuery(engineType, table.SchemaName, table.TableName, column.ColumnName);

        var commandDefinition = new CommandDefinition(
            sql,
            cancellationToken: cancellationToken,
            commandTimeout: CommandTimeoutSeconds);

        var rows = (await connection.QueryAsync(commandDefinition)).AsList();
        var values = new List<string>();

        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object> rowDict)
            {
                continue;
            }

            var value = rowDict.Values.FirstOrDefault();
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
}
