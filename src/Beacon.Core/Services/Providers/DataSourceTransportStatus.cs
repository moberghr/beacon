using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.HostData;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The "transport not verified" badge of the data-source pages: whether each data source's connection authenticates the
/// server over TLS (<see cref="IDataSourceConnectionResolver.InspectTransport"/>), without connecting. Null for a source
/// whose connection data cannot be read (encrypted under a key no longer configured, or a host connection string that is
/// missing).
/// </summary>
internal sealed class DataSourceTransportStatus(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceConnectionResolver connectionResolver)
{
    public async Task<Dictionary<int, bool?>> VerifiedAsync(IReadOnlyCollection<int> dataSourceIds, CancellationToken cancellationToken)
    {
        if (dataSourceIds.Count == 0)
        {
            return [];
        }

        var ids = dataSourceIds.ToList();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dataSources = await context.DataSources
            .Where(x => ids.Contains(x.Id))
            .Select(x =>
                new DataSource
                {
                    Id = x.Id,
                    Name = x.Name,
                    DataSourceType = x.DataSourceType,
                    DatabaseEngineType = x.DatabaseEngineType,
                    EncryptedConnectionData = x.EncryptedConnectionData,
                    HostManagedKey = x.HostManagedKey
                })
            .ToListAsync(cancellationToken);

        var verified = new Dictionary<int, bool?>();
        foreach (var dataSource in dataSources)
        {
            verified[dataSource.Id] = Verified(dataSource);
        }

        return verified;
    }

    private bool? Verified(DataSource dataSource)
    {
        try
        {
            return connectionResolver.InspectTransport(dataSource).Verified;
        }
        catch (Exception)
        {
            // The connection data cannot be read: the badge says unknown, and the source's connections fail on the same read.
            return null;
        }
    }
}
