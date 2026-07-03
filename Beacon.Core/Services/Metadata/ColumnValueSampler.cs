using System.Text.RegularExpressions;
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
    /// (one query per table) and returns the tables with enriched <c>SampleValues</c>.
    /// Sampling failures are logged and never fail the metadata refresh.
    /// </summary>
    Task<IReadOnlyList<TableMetadataDto>> EnrichWithSampleValuesAsync(
        DatabaseEngineType engineType,
        string connectionString,
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<string>? customPiiPatterns,
        CancellationToken cancellationToken = default);

    const int MaxValuesPerColumn = 5;
    const int MaxValueLength = 50;
}

internal sealed class ColumnValueSampler(
    IQueryGuardrailService guardrailService,
    ILogger<ColumnValueSampler> logger) : IColumnValueSampler
{
    private const int SampleRowCount = 5;
    private const int CommandTimeoutSeconds = 30;

    private static readonly HashSet<string> BinaryDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bytea", "blob", "binary", "varbinary", "image", "longblob", "mediumblob", "tinyblob"
    };

    // Value-shaped PII: email addresses, SSN-like, credit-card-like, phone-like (§1.6 — a benignly
    // named column can still hold PII values)
    private static readonly Regex PiiValuePattern = new(
        @"([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})|(\b\d{3}-\d{2}-\d{4}\b)|(\b(?:\d[ -]?){13,19}\b)|(\+?\d{1,3}[ .-]?\(?\d{2,4}\)?[ .-]?\d{3}[ .-]?\d{2,4})",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

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
                var samples = await SampleTableAsync(engineType, connectionString, table, cancellationToken);
                enriched.Add(table with { Columns = ApplySamples(table.Columns, samples, customPiiPatterns) });
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

    private async Task<Dictionary<string, List<string>>> SampleTableAsync(
        DatabaseEngineType engineType,
        string connectionString,
        TableMetadataDto table,
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

        return samples;
    }

    internal IReadOnlyList<ColumnMetadataDto> ApplySamples(
        IReadOnlyList<ColumnMetadataDto> columns,
        Dictionary<string, List<string>> samples,
        IReadOnlyList<string>? customPiiPatterns)
    {
        return columns
            .Select(x =>
                ShouldSkipColumn(x, customPiiPatterns) || !samples.TryGetValue(x.ColumnName, out var values) || values.Count == 0 || ContainsPiiValue(values, customPiiPatterns)
                    ? x
                    : x with { SampleValues = values })
            .ToList();
    }

    internal static bool ContainsPiiValue(IReadOnlyList<string> values, IReadOnlyList<string>? customPiiPatterns)
    {
        foreach (var value in values)
        {
            try
            {
                if (PiiValuePattern.IsMatch(value))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat a timeout as a match — safer to drop the samples than risk leaking PII
                return true;
            }

            if (customPiiPatterns is not { Count: > 0 })
            {
                continue;
            }

            foreach (var pattern in customPiiPatterns)
            {
                try
                {
                    if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    // Fail CLOSED: an unusable custom pattern means we can't clear this value, so
                    // treat it as PII and drop the sample rather than risk persisting a leak.
                    return true;
                }
            }
        }

        return false;
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
