using Snowflake.Data.Client;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.Snowflake;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddSnowflakeConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, cs => new SnowflakeDbConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.Snowflake, "Snowflake");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, SnowflakeMetadataExtractor>();
        return builder;
    }
}
