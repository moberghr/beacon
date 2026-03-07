using System.Data.SqlClient;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.SqlServer;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddSqlServerConnector(this SemanticoBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, cs => new SqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.MSSQL, "MSSQL");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, SqlServerMetadataExtractor>();
        return builder;
    }
}
