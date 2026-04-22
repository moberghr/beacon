using System.Data.Common;
using Npgsql;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.PostgreSql;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddPostgreSqlConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, cs => new NpgsqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.PostgreSQL, "PostgreSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, PostgreSqlMetadataExtractor>();
        return builder;
    }
}
