using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.BigQuery;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddBigQueryConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.BigQuery, "Google BigQuery");
        builder.Services.AddTransient<IDataSourceProvider, BigQueryProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, BigQueryTransportInspector>());
        return builder;
    }
}
