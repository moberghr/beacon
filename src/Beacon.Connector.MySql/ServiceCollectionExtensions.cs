using MySql.Data.MySqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.MySql;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddMySqlConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, cs => new MySqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MySQL, "MySQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, MySqlMetadataExtractor>();
        return builder;
    }
}
