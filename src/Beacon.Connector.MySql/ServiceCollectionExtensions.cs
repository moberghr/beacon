using MySql.Data.MySqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.MySql;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddMySqlConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, cs => new MySqlConnection(cs), x => MySqlConnection.ClearPool((MySqlConnection)x));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MySQL, "MySQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, MySqlMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, MySqlTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigrationBulkContextProvider, MySqlMigrationBulkContextProvider>());
        return builder;
    }
}
