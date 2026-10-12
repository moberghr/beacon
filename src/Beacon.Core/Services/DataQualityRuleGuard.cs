using System.Security.Claims;
using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services;

/// <summary>
/// The read-only rules for data-quality SQL (§1.5). Every rule's SQL, generated or a CustomSql rule's own, passes the
/// shared <see cref="ISqlExecutionGate"/> in the data source's dialect (regex guardrail, a single read-only SELECT, names
/// in the source's own configured database only, the host policy for host-managed sources) and must take the gate's
/// one-row cap; it then runs through the provider's
/// read-only path. A CustomSql rule is also checked when its contract is saved: only an Admin may create, change or
/// delete a contract that carries one, it never targets a host-managed data source (it could project the host's masked
/// columns), and its SQL must pass the same checks.
/// </summary>
internal static class DataQualityRuleGuard
{
    public const string HostManagedCustomSqlMessage = "Custom SQL rules are not supported on host-managed data sources.";

    public const string OwnRowLimitMessage = "Custom SQL must not set its own LIMIT, TOP or FETCH; Beacon reads one row.";

    public const string NotCappableMessage = "Rule SQL must be a single SELECT whose result Beacon can cap at one row.";

    // A rule reads only the first row of its result.
    private const int MaxRows = 1;

    /// <param name="configuredDatabase">The database the source's connection is configured for: the only one the SQL may name.</param>
    public static SqlGateRequest GateRequest(string sql, DatabaseEngineType engineType, string? hostManagedKey, string? configuredDatabase)
    {
        return new SqlGateRequest(
            sql,
            engineType.ToString(),
            EnforceReadOnly: true,
            DetectPii: false,
            CustomPiiPatterns: null,
            Catalog: null,
            BlockOnSchemaFailure: false,
            LintContext: null,
            EnableSemanticLint: false,
            MaxRows: MaxRows)
        {
            HostManagedKey = hostManagedKey,
            ConfiguredDatabase = configuredDatabase
        };
    }

    /// <summary>
    /// Why the gate's verdict keeps a rule's SQL from running, or null when it may run. Besides a blocked statement, a
    /// statement the gate could not cap at one row is refused: the provider buffers the whole result, so an outer
    /// LIMIT/TOP/FETCH of the rule's own would lift the cap. The text quotes the gate, never data.
    /// </summary>
    public static string? RejectionOf(SqlGateReport report)
    {
        if (report.Blocked)
        {
            return $"Rule SQL was rejected: {report.BlockReason ?? "it failed validation."}";
        }

        if (report.Verdicts.RowLimit.Code is SqlGateCodes.AlreadyLimited or SqlGateCodes.OwnBoundCapped)
        {
            return OwnRowLimitMessage;
        }

        return report.Verdicts.RowLimit == SqlGateVerdict.Passed ? null : NotCappableMessage;
    }

    public static bool HasCustomSql(IEnumerable<DataContractRuleData> rules)
    {
        return rules.Any(x => x.RuleType == DataContractRuleType.CustomSql);
    }

    /// <summary>Throws <see cref="BeaconForbiddenException"/> unless the caller holds the Admin role.</summary>
    public static void EnsureAdmin(IBeaconUserContext userContext)
    {
        // The same role the BeaconApiAdmin policy requires. API-key principals carry no role, so a key never passes.
        if (!userContext.HasClaim(ClaimTypes.Role, RoleService.RoleNames.Admin))
        {
            throw new BeaconForbiddenException("Only an Admin can create, change or delete a data contract with a Custom SQL rule.");
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a CustomSql rule in <paramref name="rules"/>, enabled or not,
    /// cannot run on <paramref name="dataSource"/>: a missing, host-managed or non-database source, a configuration
    /// without SQL, or SQL the read-only gate rejects or cannot cap at one row.
    /// </summary>
    public static void EnsureCustomSqlRunnable(
        IEnumerable<DataContractRuleData> rules,
        DataQualityRuleTarget? dataSource,
        ISqlExecutionGate gate)
    {
        foreach (var rule in rules.Where(x => x.RuleType == DataContractRuleType.CustomSql))
        {
            if (dataSource == null)
            {
                throw new InvalidOperationException("The contract's data source was not found.");
            }

            if (dataSource.HostManagedKey != null)
            {
                throw new InvalidOperationException(HostManagedCustomSqlMessage);
            }

            if (dataSource.DataSourceType != DataSourceType.Database || dataSource.DatabaseEngineType == null)
            {
                throw new InvalidOperationException("Custom SQL rules need a database data source.");
            }

            var report = gate.Evaluate(GateRequest(ReadSql(rule), dataSource.DatabaseEngineType.Value, hostManagedKey: null, dataSource.ConfiguredDatabase));
            var rejection = RejectionOf(report);
            if (rejection != null)
            {
                throw new InvalidOperationException($"Custom SQL rule '{rule.Name}': {rejection}");
            }
        }
    }

    /// <summary>
    /// The contract's table and its <paramref name="rules"/> with every table and column they name spelled as the contract
    /// data source's metadata spells it (<see cref="DataQualityRuleCatalog"/>), for storing. Throws
    /// <see cref="InvalidOperationException"/> when one of them is not an object of that data source.
    /// </summary>
    public static ConfinedContract EnsureConfined(
        DataQualityRuleCatalog catalog,
        string schemaName,
        string tableName,
        IReadOnlyList<DataContractRuleData> rules)
    {
        var table = catalog.TableOf(schemaName, tableName, out var tableProblem)
            ?? throw new InvalidOperationException(tableProblem);

        var confined = new List<ConfinedRule>();
        foreach (var rule in rules)
        {
            var confinedRule = catalog.Confine(schemaName, tableName, rule.RuleType, rule.ColumnName, rule.Configuration);
            if (confinedRule.Problem != null)
            {
                throw new InvalidOperationException($"Rule '{rule.Name}': {confinedRule.Problem}");
            }

            confined.Add(confinedRule);
        }

        return new ConfinedContract(table.Schema, table.Table, confined);
    }

    private static string ReadSql(DataContractRuleData rule)
    {
        return TryReadSql(rule.Configuration)
            ?? throw new InvalidOperationException($"Custom SQL rule '{rule.Name}' needs a \"sql\" string in its configuration.");
    }

    private static string? TryReadSql(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        try
        {
            using var config = JsonDocument.Parse(configuration);
            var sql = config.RootElement.ValueKind == JsonValueKind.Object
                && config.RootElement.TryGetProperty("sql", out var element)
                && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

            return string.IsNullOrWhiteSpace(sql) ? null : sql;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A contract's table and rules as <see cref="DataQualityRuleGuard.EnsureConfined"/> resolved them: <see cref="Rules"/>
/// follows the order of the rules it was given.
/// </summary>
internal sealed record ConfinedContract(string SchemaName, string TableName, IReadOnlyList<ConfinedRule> Rules);

/// <summary>
/// The data source facts a CustomSql rule is checked against when its contract is saved; <paramref name="ConfiguredDatabase"/>
/// is the database its connection is configured for (null for a host-managed source, whose CustomSql rules are refused).
/// </summary>
internal sealed record DataQualityRuleTarget(
    DataSourceType DataSourceType,
    DatabaseEngineType? DatabaseEngineType,
    string? HostManagedKey,
    string? ConfiguredDatabase)
{
    public static DataQualityRuleTarget? Of(DataSource? dataSource, IConfiguredDatabaseResolver configuredDatabases)
    {
        if (dataSource == null)
        {
            return null;
        }

        var configuredDatabase = dataSource.HostManagedKey == null ? configuredDatabases.Resolve(dataSource) : null;

        return new DataQualityRuleTarget(dataSource.DataSourceType, dataSource.DatabaseEngineType, dataSource.HostManagedKey, configuredDatabase);
    }
}
