using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.Databricks;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddDatabricksConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Databricks, "Databricks");
        builder.Services.AddTransient<IDataSourceProvider, DatabricksProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, DatabricksTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceConnectionOptionInspector, DatabricksConnectionOptionInspector>());
        return builder;
    }
}
