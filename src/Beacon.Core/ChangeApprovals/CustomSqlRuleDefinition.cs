using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// What an approval of a Custom SQL rule covers: its SQL (character for character) on the data source it runs on. A rule
/// whose SQL or data source changes is a new definition; its name, severity, weight and the rest of its configuration are
/// not part of it. A contract's change request covers the set of its rules waiting for approval (<see cref="SetHash"/>).
/// </summary>
internal static class CustomSqlRuleDefinition
{
    /// <summary>The SHA-256 of a Custom SQL rule's SQL (read from its configuration) on <paramref name="dataSourceId"/>.</summary>
    public static string Hash(int dataSourceId, string? configuration)
    {
        var sql = DataQualityRuleGuard.TryReadSql(configuration) ?? string.Empty;

        return ChangeApprovalHashes.Sha256($"beacon:custom-sql-rule:v1\n{dataSourceId}\n{sql}");
    }

    /// <summary>The SHA-256 of a set of rule hashes, in any order (a rule listed twice counts twice).</summary>
    public static string SetHash(IEnumerable<string> ruleHashes)
    {
        return ChangeApprovalHashes.Sha256("beacon:custom-sql-rules:v1\n" + string.Join("\n", ruleHashes.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// True when <paramref name="rule"/> is a governed Custom SQL rule whose SQL on <paramref name="dataSourceId"/> is not
    /// the approved one: it waits for approval and is not evaluated.
    /// </summary>
    public static bool Awaits(DataContractRule rule, int dataSourceId)
    {
        return rule.RuleType == DataContractRuleType.CustomSql
            && !ChangeApprovalHashes.Allows(rule.ApprovedSqlHash, Hash(dataSourceId, rule.Configuration));
    }
}
