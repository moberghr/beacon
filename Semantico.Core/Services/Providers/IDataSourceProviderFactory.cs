using Semantico.Core.Data.Enums;

namespace Semantico.Core.Services.Providers;

public interface IDataSourceProviderFactory
{
    /// <summary>
    /// Gets the appropriate provider for the specified data source type
    /// </summary>
    IDataSourceProvider GetProvider(DataSourceType dataSourceType);
}
