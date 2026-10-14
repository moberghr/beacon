using Beacon.Core.Data.Entities;

namespace Beacon.Core.HostData;

/// <summary>
/// Execution-time enforcement for host-managed data sources, applied at every choke point that runs SQL
/// (the execution gate, <c>DatabaseProvider</c>, saved-query steps, data-quality rules). Ordinary data sources
/// pass straight through. The rows a host data source returns are masked by <see cref="HostResultMasker"/>, by output
/// position against the <see cref="HostPolicyResult.OutputColumns"/> of the check.
/// </summary>
public interface IHostDataSourceGuard
{
    /// <summary>Checks <paramref name="sql"/> against the source's host policy; always allowed for ordinary sources.</summary>
    HostPolicyResult Check(DataSource dataSource, string sql);

    /// <summary>Checks <paramref name="sql"/> against the policy registered under <paramref name="hostManagedKey"/>.</summary>
    HostPolicyResult Check(string hostManagedKey, string sql);
}

internal sealed class HostDataSourceGuard(IHostDataSourceRegistry registry) : IHostDataSourceGuard
{
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
}
