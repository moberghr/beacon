using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.Databricks;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddDatabricksConnector(this SemanticoBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Databricks, "Databricks");
        builder.Services.AddTransient<IDataSourceProvider, DatabricksProvider>();
        return builder;
    }
}
