using MySql.Data.MySqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
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
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, cs => new MySqlConnection(WithConnectTimeout(cs)), x => MySqlConnection.ClearPool((MySqlConnection)x));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MySQL, "MySQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, MySqlMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, MySqlTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceConnectionOptionInspector, MySqlConnectionOptionInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigrationBulkWriter, MySqlMigrationBulkWriter>());
        return builder;
    }

    // The connect timeout (Connection Timeout) bounded as DbConnectionFactory.ConnectTimeoutSeconds sets it; a string
    // already within the bounds is used as it is. Statement batches (AllowBatch) are left as the caller set them.
    private static string WithConnectTimeout(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var timeout = (uint)DbConnectionFactory.ConnectTimeoutSeconds(builder.ConnectionTimeout);
        if (timeout == builder.ConnectionTimeout)
        {
            return connectionString;
        }

        builder.ConnectionTimeout = timeout;

        return builder.ConnectionString;
    }
}
