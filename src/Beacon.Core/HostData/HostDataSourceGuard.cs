using Beacon.Core.Data.Entities;

namespace Beacon.Core.HostData;

/// <summary>
/// Execution-time enforcement for host-managed data sources, applied at every choke point that runs SQL
/// (the execution gate, <c>DatabaseProvider</c>, saved-query steps, data-quality rules). Ordinary data sources
/// pass straight through.
/// </summary>
public interface IHostDataSourceGuard
{
    /// <summary>Checks <paramref name="sql"/> against the source's host policy; always allowed for ordinary sources.</summary>
    HostPolicyResult Check(DataSource dataSource, string sql);

    /// <summary>Checks <paramref name="sql"/> against the policy registered under <paramref name="hostManagedKey"/>.</summary>
    HostPolicyResult Check(string hostManagedKey, string sql);

    /// <summary>
    /// FULLY masks the values of <paramref name="maskedColumns"/> (matched by result key, case-insensitively): every
    /// non-null value becomes <see cref="HostDataSourceGuard.MaskedValue"/>, null stays null. Unlike the PII
    /// guardrail's partial masking, no character of a host-masked value is ever returned.
    /// </summary>
    List<Dictionary<string, object?>> Mask(List<Dictionary<string, object?>> rows, IReadOnlyList<string> maskedColumns);
}

internal sealed class HostDataSourceGuard(IHostDataSourceRegistry registry) : IHostDataSourceGuard
{
    /// <summary>What a host-masked value is replaced with.</summary>
    public const string MaskedValue = "***";

    private static readonly HostPolicyResult NotHostManaged = new(true, null, []);

    public HostPolicyResult Check(DataSource dataSource, string sql)
    {
        return dataSource.HostManagedKey == null ? NotHostManaged : Check(dataSource.HostManagedKey, sql);
    }

    public HostPolicyResult Check(string hostManagedKey, string sql)
    {
        HostExposureSnapshot? snapshot;
        try
        {
            snapshot = registry.GetSnapshot(hostManagedKey);
        }
        catch (InvalidOperationException ex)
        {
            return HostPolicyResult.Reject($"Host data source configuration is invalid: {ex.Message}");
        }

        if (snapshot == null)
        {
            // The row outlived its ExposeDbContext registration (or another host owns it): expose nothing.
            return HostPolicyResult.Reject("This host data source is not registered by the running host, so it cannot be queried.");
        }

        return HostQueryPolicyValidator.Validate(sql, snapshot.Policy);
    }

    public List<Dictionary<string, object?>> Mask(List<Dictionary<string, object?>> rows, IReadOnlyList<string> maskedColumns)
    {
        if (maskedColumns.Count == 0 || rows.Count == 0)
        {
            return rows;
        }

        var masked = new HashSet<string>(maskedColumns, StringComparer.OrdinalIgnoreCase);

        return rows
            .Select(x => x.ToDictionary(y => y.Key, y => y.Value != null && masked.Contains(y.Key) ? MaskedValue : y.Value))
            .ToList();
    }
}
