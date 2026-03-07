using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.BigQuery;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddBigQueryConnector(this SemanticoBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.BigQuery, "Google BigQuery");
        builder.Services.AddTransient<IDataSourceProvider, BigQueryProvider>();
        return builder;
    }
}
