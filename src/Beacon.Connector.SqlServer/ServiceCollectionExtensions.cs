using Microsoft.Data.SqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.SqlServer;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddSqlServerConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, cs => new SqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MSSQL, "MSSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, SqlServerMetadataExtractor>();
        return builder;
    }
}
