using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// Keeps a data contract's rules on the contract's own data source: every table and column a rule names — the contract's
/// table, a rule's column, a Referential rule's reference table and column — must be an object of that data source's
/// stored metadata. A name is resolved to the metadata's spelling: an exact match first, otherwise the one name that
/// matches without regard to case. A name matching none, or several that differ only in case (on a case-sensitive server
/// they are different objects), is refused. The generated SQL quotes the resolved names as identifiers, so a rule can
/// then name no other schema, database or catalog the data source's login can see. Columns are checked only where the
/// metadata lists a table's columns. A CustomSql rule's own SQL is not read here: the read-only gate checks it.
/// </summary>
internal sealed class DataQualityRuleCatalog
{
    private readonly List<CatalogTable> _tables;

    private DataQualityRuleCatalog(List<CatalogTable> tables)
    {
        _tables = tables;
    }

    /// <summary>
    /// The metadata of the tables the rules (their <paramref name="configurations"/>) and the contract's own table name, on
    /// <paramref name="dataSourceId"/> only, matched without regard to case.
    /// </summary>
    public static async Task<DataQualityRuleCatalog> LoadAsync(
        BeaconContext context,
        int dataSourceId,
        string schemaName,
        string tableName,
        IEnumerable<string?> configurations,
        CancellationToken cancellationToken)
    {
        var referenced = configurations
            .Select(x => ReferenceTableOf(Parse(x), schemaName))
            .OfType<(string Schema, string Table)>()
            .Append<(string Schema, string Table)>((schemaName, tableName))
            .ToList();
        var schemas = referenced
            .Select(x => x.Schema.ToLowerInvariant())
            .Distinct()
            .ToList();
        var tables = referenced
            .Select(x => x.Table.ToLowerInvariant())
            .Distinct()
            .ToList();

        var rows = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => schemas.Contains(x.SchemaName.ToLower()))
            .Where(x => tables.Contains(x.TableName.ToLower()))
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

        var known = rows
            .Select(x => new CatalogTable(x.SchemaName, x.TableName, x.Columns))
            .ToList();

        return new DataQualityRuleCatalog(known);
    }

    /// <summary>
    /// The rule of the contract on <paramref name="schemaName"/>.<paramref name="tableName"/> with every object it names
    /// spelled as the metadata spells it, or why it names an object outside the data source's metadata.
    /// </summary>
    public ConfinedRule Confine(
        string schemaName,
        string tableName,
        DataContractRuleType ruleType,
        string? columnName,
        string? configuration)
    {
        var table = Resolve(schemaName, tableName, out var tableProblem);
        if (table == null)
        {
            return ConfinedRule.Refused(tableProblem!);
        }

        if (ruleType == DataContractRuleType.CustomSql)
        {
            return ConfinedRule.Allowed(table.Schema, table.Table, columnName, configuration);
        }

        var resolvedColumn = ResolveColumn(table, columnName, out var columnProblem);
        if (columnProblem != null)
        {
            return ConfinedRule.Refused(columnProblem);
        }

        var config = Parse(configuration);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var configColumn = StringOf(config, "column");
        var resolvedConfigColumn = ResolveColumn(table, configColumn, out columnProblem);
        if (columnProblem != null)
        {
            return ConfinedRule.Refused(columnProblem);
        }

        Rename(renames, "column", configColumn, resolvedConfigColumn);

        var referenceTable = ruleType == DataContractRuleType.Referential ? StringOf(config, "referenceTable") : null;
        if (string.IsNullOrEmpty(referenceTable))
        {
            return ConfinedRule.Allowed(table.Schema, table.Table, resolvedColumn, Rewrite(configuration, renames));
        }

        // The reference schema defaults to the contract's, as in the generated SQL.
        var referenceSchema = StringOf(config, "referenceSchema");
        var reference = Resolve(referenceSchema ?? table.Schema, referenceTable, out var referenceProblem);
        if (reference == null)
        {
            return ConfinedRule.Refused(referenceProblem!);
        }

        Rename(renames, "referenceSchema", referenceSchema, reference.Schema);
        Rename(renames, "referenceTable", referenceTable, reference.Table);

        var referenceColumn = StringOf(config, "referenceColumn");
        var resolvedReferenceColumn = ResolveColumn(reference, referenceColumn, out columnProblem);
        if (columnProblem != null)
        {
            return ConfinedRule.Refused(columnProblem);
        }

        Rename(renames, "referenceColumn", referenceColumn, resolvedReferenceColumn);

        return ConfinedRule.Allowed(table.Schema, table.Table, resolvedColumn, Rewrite(configuration, renames));
    }

    /// <summary>
    /// Why a rule of the contract on <paramref name="schemaName"/>.<paramref name="tableName"/> names an object outside the
    /// data source's metadata, or null when every object it names is the data source's.
    /// </summary>
    public string? UnknownObjectOf(
        string schemaName,
        string tableName,
        DataContractRuleType ruleType,
        string? columnName,
        string? configuration)
    {
        return Confine(schemaName, tableName, ruleType, columnName, configuration).Problem;
    }

    /// <summary>The contract's own table as the metadata spells it; null, with the reason, when it is not the data source's.</summary>
    public (string Schema, string Table)? TableOf(string schemaName, string tableName, out string? problem)
    {
        var table = Resolve(schemaName, tableName, out problem);

        return table == null ? null : (table.Schema, table.Table);
    }

    /// <summary>Why the contract's own table is outside the data source's metadata, or null when it is the data source's.</summary>
    public string? UnknownTableOf(string schemaName, string tableName)
    {
        TableOf(schemaName, tableName, out var problem);

        return problem;
    }

    // An exact match, else the one table matching without regard to case.
    private CatalogTable? Resolve(string schemaName, string tableName, out string? problem)
    {
        problem = null;
        var exact = _tables
            .Where(x => x.Schema == schemaName)
            .Where(x => x.Table == tableName)
            .FirstOrDefault();
        if (exact != null)
        {
            return exact;
        }

        var matches = _tables
            .Where(x => string.Equals(x.Schema, schemaName, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(x.Table, tableName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        problem = matches.Count == 0
            ? $"Table {schemaName}.{tableName} is not in the data source's metadata: a rule may only name the data source's own tables. Refresh the data source's metadata if the table is new."
            : $"Table {schemaName}.{tableName} matches several tables of the data source's metadata that differ only in case. Write the name exactly as the metadata spells it.";

        return null;
    }

    // The column as the metadata spells it; the name as given when the table's columns are not listed.
    private static string? ResolveColumn(CatalogTable table, string? columnName, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(columnName) || table.Columns.Count == 0 || table.Columns.Contains(columnName, StringComparer.Ordinal))
        {
            return columnName;
        }

        var matches = table.Columns
            .Where(x => string.Equals(x, columnName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        problem = matches.Count == 0
            ? $"Column {columnName} is not a column of {table.Schema}.{table.Table} in the data source's metadata."
            : $"Column {columnName} matches several columns of {table.Schema}.{table.Table} that differ only in case. Write the name exactly as the metadata spells it.";

        return null;
    }

    private static void Rename(Dictionary<string, string> renames, string property, string? given, string? resolved)
    {
        if (given != null && resolved != null && !string.Equals(given, resolved, StringComparison.Ordinal))
        {
            renames[property] = resolved;
        }
    }

    // The configuration with the resolved spellings; untouched when nothing was renamed.
    private static string? Rewrite(string? configuration, Dictionary<string, string> renames)
    {
        if (renames.Count == 0 || configuration == null)
        {
            return configuration;
        }

        var root = JsonNode.Parse(configuration)!.AsObject();
        foreach (var (property, value) in renames)
        {
            root[property] = value;
        }

        return root.ToJsonString();
    }

    // A Referential rule's reference table; its schema defaults to the contract's, as in the generated SQL.
    private static (string Schema, string Table)? ReferenceTableOf(JsonElement? config, string schemaName)
    {
        var referenceTable = StringOf(config, "referenceTable");
        if (string.IsNullOrEmpty(referenceTable))
        {
            return null;
        }

        return (StringOf(config, "referenceSchema") ?? schemaName, referenceTable);
    }

    // The configuration's root object, or null when it is not a JSON object: such a rule cannot be generated, so it is
    // refused when it runs.
    private static JsonElement? Parse(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);

            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement? config, string property)
    {
        return config is { } root
            && root.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
    }

    private sealed record CatalogTable(string Schema, string Table, List<string> Columns);
}

/// <summary>
/// A data-quality rule confined to its data source: the contract's table and the rule's column and configuration with
/// every name they give spelled as the metadata spells it, or the <see cref="Problem"/> that keeps the rule from being
/// saved or run.
/// </summary>
internal sealed record ConfinedRule(string? Problem, string SchemaName, string TableName, string? ColumnName, string? Configuration)
{
    public static ConfinedRule Refused(string problem) => new(problem, "", "", null, null);

    public static ConfinedRule Allowed(string schemaName, string tableName, string? columnName, string? configuration) =>
        new(null, schemaName, tableName, columnName, configuration);
}
