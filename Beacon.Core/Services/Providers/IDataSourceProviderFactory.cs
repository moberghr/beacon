using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

public interface IDataSourceProviderFactory
{
    /// <summary>
    /// Gets the appropriate provider for the specified data source type
    /// </summary>
    IDataSourceProvider GetProvider(DataSourceType dataSourceType);
}
