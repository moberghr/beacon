using System.Data.Common;
using Npgsql;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.PostgreSql;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddPostgreSqlConnector(this SemanticoBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, cs => new NpgsqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.PostgreSQL, "PostgreSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, PostgreSqlMetadataExtractor>();
        return builder;
    }
}
