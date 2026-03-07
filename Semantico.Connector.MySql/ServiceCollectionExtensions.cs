using MySql.Data.MySqlClient;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.MySql;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddMySqlConnector(this SemanticoBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, cs => new MySqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MySQL, "MySQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, MySqlMetadataExtractor>();
        return builder;
    }
}
