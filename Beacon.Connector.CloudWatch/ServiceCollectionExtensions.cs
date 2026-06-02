using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.CloudWatch;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddCloudWatchConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.CloudWatch, "AWS CloudWatch");
        builder.Services.AddTransient<IDataSourceProvider, CloudWatchProvider>();
        return builder;
    }
}
