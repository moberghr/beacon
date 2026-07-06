using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Providers;

internal class DataSourceProviderFactory(IEnumerable<IDataSourceProvider> providers) : IDataSourceProviderFactory
{
    private readonly Dictionary<DataSourceType, IDataSourceProvider> _providerMap = providers
        .ToDictionary(p => p.SupportedType, p => p);

    public IDataSourceProvider GetProvider(DataSourceType dataSourceType)
    {
        if (_providerMap.TryGetValue(dataSourceType, out var provider))
        {
            return provider;
        }

        throw new BeaconException($"No provider registered for data source type: {dataSourceType}");
    }
}
