using Snowflake.Data.Client;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.Snowflake;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddSnowflakeConnector(this SemanticoBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, cs => new SnowflakeDbConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.Snowflake, "Snowflake");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, SnowflakeMetadataExtractor>();
        return builder;
    }
}
