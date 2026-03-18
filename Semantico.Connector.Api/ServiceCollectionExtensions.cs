using Microsoft.Extensions.DependencyInjection;
using Semantico.Connector.Api.Services;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Providers;

namespace Semantico.Connector.Api;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddApiConnector(this SemanticoBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Api, "REST API");
        builder.Services.AddTransient<IDataSourceProvider, ApiProvider>();
        builder.Services.AddTransient<OpenApiImportService>();
        builder.Services.AddTransient<JsonResponseTabularizer>();
        return builder;
    }
}
