using Semantico.Core.Data.Enums;
using Semantico.Core.Models;

namespace Semantico.Core.Services.Providers;

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

        throw new SemanticoException($"No provider registered for data source type: {dataSourceType}");
    }
}
