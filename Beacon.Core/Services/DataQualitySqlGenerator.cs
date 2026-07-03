using System.Text.Json;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

public interface IDataQualitySqlGenerator
{
    string GenerateSql(DataContractRule rule, DatabaseEngineType engineType);
}

internal class DataQualitySqlGenerator : IDataQualitySqlGenerator
{
    public string GenerateSql(DataContractRule rule, DatabaseEngineType engineType)
    {
        var config = new RuleConfig(rule.Configuration);

        return rule.RuleType switch
        {
            DataContractRuleType.Freshness => GenerateFreshnessSql(config, engineType),
            DataContractRuleType.Volume => GenerateVolumeSql(config, engineType),
            DataContractRuleType.NullRate => GenerateNullRateSql(config, engineType),
            DataContractRuleType.Uniqueness => GenerateUniquenessSql(config, engineType),
            DataContractRuleType.Referential => GenerateReferentialSql(config, engineType),
            DataContractRuleType.Range => GenerateRangeSql(config, engineType),
            DataContractRuleType.Pattern => GeneratePatternSql(config, engineType),
            DataContractRuleType.CustomSql => GenerateCustomSql(config),
            _ => throw new ArgumentOutOfRangeException(nameof(rule.RuleType), rule.RuleType, "Unsupported rule type")
        };
    }

    private static string GenerateFreshnessSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = SqlIdentifierGuard.Validate(config.GetString("column"), "column");
        var maxAgeMinutes = config.GetInt("maxAgeMinutes");
        var qualifiedTable = QualifyTable(schema, table, engineType);

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL =>
                $"SELECT CASE WHEN MAX(\"{column}\") < NOW() - INTERVAL '{maxAgeMinutes} minutes' THEN 1 ELSE 0 END AS failed, " +
                $"EXTRACT(EPOCH FROM (NOW() - MAX(\"{column}\"))) / 60 AS actual_value FROM {qualifiedTable}",
            DatabaseEngineType.MSSQL =>
                $"SELECT CASE WHEN MAX([{column}]) < DATEADD(MINUTE, -{maxAgeMinutes}, GETUTCDATE()) THEN 1 ELSE 0 END AS failed, " +
                $"DATEDIFF(MINUTE, MAX([{column}]), GETUTCDATE()) AS actual_value FROM {qualifiedTable}",
            DatabaseEngineType.MySQL =>
                $"SELECT CASE WHEN MAX(`{column}`) < DATE_SUB(UTC_TIMESTAMP(), INTERVAL {maxAgeMinutes} MINUTE) THEN 1 ELSE 0 END AS failed, " +
                $"TIMESTAMPDIFF(MINUTE, MAX(`{column}`), UTC_TIMESTAMP()) AS actual_value FROM {qualifiedTable}",
            _ => throw new NotSupportedException($"Engine {engineType} not supported for Freshness rule")
        };
    }

    private static string GenerateVolumeSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var qualifiedTable = QualifyTable(schema, table, engineType);

        return $"SELECT COUNT(*) AS row_count FROM {qualifiedTable}";
    }

    private static string GenerateNullRateSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = SqlIdentifierGuard.Validate(config.GetString("column"), "column");
        var qualifiedTable = QualifyTable(schema, table, engineType);

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL =>
                $"SELECT COUNT(*) FILTER (WHERE \"{column}\" IS NULL) * 100.0 / NULLIF(COUNT(*), 0) AS null_percent, " +
                $"COUNT(*) AS total FROM {qualifiedTable}",
            DatabaseEngineType.MSSQL =>
                $"SELECT SUM(CASE WHEN [{column}] IS NULL THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS null_percent, " +
                $"COUNT(*) AS total FROM {qualifiedTable}",
            DatabaseEngineType.MySQL =>
                $"SELECT SUM(CASE WHEN `{column}` IS NULL THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS null_percent, " +
                $"COUNT(*) AS total FROM {qualifiedTable}",
            _ => throw new NotSupportedException($"Engine {engineType} not supported for NullRate rule")
        };
    }

    private static string GenerateUniquenessSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = SqlIdentifierGuard.Validate(config.GetString("column"), "column");
        var qualifiedTable = QualifyTable(schema, table, engineType);

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL =>
                $"SELECT COUNT(*) - COUNT(DISTINCT \"{column}\") AS duplicate_count, COUNT(*) AS total FROM {qualifiedTable}",
            DatabaseEngineType.MSSQL =>
                $"SELECT COUNT(*) - COUNT(DISTINCT [{column}]) AS duplicate_count, COUNT(*) AS total FROM {qualifiedTable}",
            DatabaseEngineType.MySQL =>
                $"SELECT COUNT(*) - COUNT(DISTINCT `{column}`) AS duplicate_count, COUNT(*) AS total FROM {qualifiedTable}",
            _ => throw new NotSupportedException($"Engine {engineType} not supported for Uniqueness rule")
        };
    }

    private static string GenerateReferentialSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = SqlIdentifierGuard.Validate(config.GetString("column"), "column");
        var refSchema = config.GetStringOrDefault("referenceSchema", schema);
        var refTable = config.GetString("referenceTable");
        var refColumn = SqlIdentifierGuard.Validate(config.GetString("referenceColumn"), "column");

        var qualifiedTable = QualifyTable(schema, table, engineType);
        var qualifiedRefTable = QualifyTable(refSchema, refTable, engineType);

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL =>
                $"SELECT COUNT(*) AS orphaned FROM {qualifiedTable} t " +
                $"LEFT JOIN {qualifiedRefTable} r ON t.\"{column}\" = r.\"{refColumn}\" " +
                $"WHERE r.\"{refColumn}\" IS NULL AND t.\"{column}\" IS NOT NULL",
            DatabaseEngineType.MSSQL =>
                $"SELECT COUNT(*) AS orphaned FROM {qualifiedTable} t " +
                $"LEFT JOIN {qualifiedRefTable} r ON t.[{column}] = r.[{refColumn}] " +
                $"WHERE r.[{refColumn}] IS NULL AND t.[{column}] IS NOT NULL",
            DatabaseEngineType.MySQL =>
                $"SELECT COUNT(*) AS orphaned FROM {qualifiedTable} t " +
                $"LEFT JOIN {qualifiedRefTable} r ON t.`{column}` = r.`{refColumn}` " +
                $"WHERE r.`{refColumn}` IS NULL AND t.`{column}` IS NOT NULL",
            _ => throw new NotSupportedException($"Engine {engineType} not supported for Referential rule")
        };
    }

    private static string GenerateRangeSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = config.GetString("column");
        var min = config.GetStringOrDefault("min", null);
        var max = config.GetStringOrDefault("max", null);
        var qualifiedTable = QualifyTable(schema, table, engineType);

        var conditions = new List<string>();
        if (min != null)
        {
            ValidateNumeric(min, "min");
            var quotedCol = QuoteColumn(column, engineType);
            conditions.Add($"{quotedCol} < {min}");
        }
        if (max != null)
        {
            ValidateNumeric(max, "max");
            var quotedCol = QuoteColumn(column, engineType);
            conditions.Add($"{quotedCol} > {max}");
        }

        var whereClause = conditions.Count > 0 ? string.Join(" OR ", conditions) : "1=0";

        return $"SELECT COUNT(*) AS out_of_range, (SELECT COUNT(*) FROM {qualifiedTable}) AS total FROM {qualifiedTable} WHERE {whereClause}";
    }

    private static string GeneratePatternSql(RuleConfig config, DatabaseEngineType engineType)
    {
        var schema = config.GetString("schema");
        var table = config.GetString("table");
        var column = SqlIdentifierGuard.Validate(config.GetString("column"), "column");
        var pattern = config.GetString("pattern");
        var qualifiedTable = QualifyTable(schema, table, engineType);

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL =>
                $"SELECT COUNT(*) AS non_matching FROM {qualifiedTable} WHERE \"{column}\" !~ '{EscapeSqlString(pattern)}'",
            DatabaseEngineType.MySQL =>
                $"SELECT COUNT(*) AS non_matching FROM {qualifiedTable} WHERE `{column}` NOT REGEXP '{EscapeSqlString(pattern)}'",
            DatabaseEngineType.MSSQL =>
                $"SELECT COUNT(*) AS non_matching FROM {qualifiedTable} WHERE [{column}] NOT LIKE '{EscapeSqlString(pattern)}'",
            _ => throw new NotSupportedException($"Engine {engineType} not supported for Pattern rule")
        };
    }

    private static string GenerateCustomSql(RuleConfig config)
    {
        return config.GetString("sql");
    }

    private static string QualifyTable(string schema, string table, DatabaseEngineType engineType)
    {
        SqlIdentifierGuard.Validate(schema, "schema");
        SqlIdentifierGuard.Validate(table, "table");

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => $"\"{schema}\".\"{table}\"",
            DatabaseEngineType.MSSQL => $"[{schema}].[{table}]",
            DatabaseEngineType.MySQL => $"`{schema}`.`{table}`",
            _ => $"{schema}.{table}"
        };
    }

    private static string QuoteColumn(string column, DatabaseEngineType engineType)
    {
        SqlIdentifierGuard.Validate(column, "column");

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => $"\"{column}\"",
            DatabaseEngineType.MSSQL => $"[{column}]",
            DatabaseEngineType.MySQL => $"`{column}`",
            _ => column
        };
    }

    private static string ValidateNumeric(string value, string property)
    {
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException($"Rule config property '{property}' must be numeric.");
        }

        return value;
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''");

    private class RuleConfig
    {
        private readonly JsonElement _root;

        public RuleConfig(string json)
        {
            _root = JsonDocument.Parse(json).RootElement;
        }

        public string GetString(string property)
        {
            if (_root.TryGetProperty(property, out var element))
                return element.GetString() ?? throw new InvalidOperationException($"Rule config property '{property}' is null");

            throw new InvalidOperationException($"Rule config missing required property '{property}'");
        }

        public string? GetStringOrDefault(string property, string? defaultValue)
        {
            if (_root.TryGetProperty(property, out var element))
                return element.GetString() ?? defaultValue;

            return defaultValue;
        }

        public int GetInt(string property)
        {
            if (_root.TryGetProperty(property, out var element))
                return element.GetInt32();

            throw new InvalidOperationException($"Rule config missing required property '{property}'");
        }

        public double GetDouble(string property)
        {
            if (_root.TryGetProperty(property, out var element))
                return element.GetDouble();

            throw new InvalidOperationException($"Rule config missing required property '{property}'");
        }

        public double? GetDoubleOrDefault(string property)
        {
            if (_root.TryGetProperty(property, out var element))
                return element.GetDouble();

            return null;
        }
    }
}
