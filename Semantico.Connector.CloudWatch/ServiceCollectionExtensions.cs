using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.CloudWatch;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddCloudWatchConnector(this SemanticoBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.CloudWatch, "AWS CloudWatch");
        builder.Services.AddTransient<IDataSourceProvider, CloudWatchProvider>();
        return builder;
    }
}
