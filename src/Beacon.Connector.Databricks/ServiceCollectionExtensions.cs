using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.Databricks;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddDatabricksConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Databricks, "Databricks");
        builder.Services.AddTransient<IDataSourceProvider, DatabricksProvider>();
        return builder;
    }
}
