using Microsoft.Extensions.DependencyInjection;
using Beacon.Connector.Api.Services;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddApiConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Api, "REST API");
        builder.Services.AddTransient<IDataSourceProvider, ApiProvider>();
        builder.Services.AddTransient<OpenApiImportService>();
        builder.Services.AddTransient<JsonResponseTabularizer>();
        return builder;
    }
}
