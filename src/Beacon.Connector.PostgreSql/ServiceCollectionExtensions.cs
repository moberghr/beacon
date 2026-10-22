using System.Data.Common;
using Npgsql;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.PostgreSql;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddPostgreSqlConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, cs => new NpgsqlConnection(WithConnectTimeout(cs)), x => NpgsqlConnection.ClearPool((NpgsqlConnection)x));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.PostgreSQL, "PostgreSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, PostgreSqlMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, PostgreSqlTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigrationBulkWriter, PostgreSqlMigrationBulkWriter>());
        return builder;
    }

    // The connect timeout (Timeout) bounded as DbConnectionFactory.ConnectTimeoutSeconds sets it; a string already within
    // the bounds is used as it is.
    private static string WithConnectTimeout(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var timeout = DbConnectionFactory.ConnectTimeoutSeconds(builder.Timeout);
        if (timeout == builder.Timeout)
        {
            return connectionString;
        }

        builder.Timeout = timeout;

        return builder.ConnectionString;
    }
}
