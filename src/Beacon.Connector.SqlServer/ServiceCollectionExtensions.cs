using Microsoft.Data.SqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.SqlServer;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddSqlServerConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, cs => new SqlConnection(WithConnectTimeout(cs)), x => SqlConnection.ClearPool((SqlConnection)x));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MSSQL, "MSSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, SqlServerMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, SqlServerTransportInspector>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMigrationBulkWriter, SqlServerMigrationBulkWriter>());
        return builder;
    }

    // The connect timeout (Connect Timeout) bounded as DbConnectionFactory.ConnectTimeoutSeconds sets it; a string already
    // within the bounds is used as it is.
    private static string WithConnectTimeout(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var timeout = DbConnectionFactory.ConnectTimeoutSeconds(builder.ConnectTimeout);
        if (timeout == builder.ConnectTimeout)
        {
            return connectionString;
        }

        builder.ConnectTimeout = timeout;

        return builder.ConnectionString;
    }
}
