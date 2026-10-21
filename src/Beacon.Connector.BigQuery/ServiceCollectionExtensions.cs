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
        builder.Services.AddOptions<BigQueryOptions>()
            .Bind(builder.Configuration.GetSection(BigQueryOptions.SectionName))
            .Validate(x => x.MaxBytesBilled is null or > 0, $"{BigQueryOptions.SectionName}:{nameof(BigQueryOptions.MaxBytesBilled)} must be at least 1 when set.")
            .ValidateOnStart();
        builder.Services.AddTransient<IDataSourceProvider, BigQueryProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, BigQueryTransportInspector>());
        return builder;
    }
}
