using Microsoft.Data.SqlClient;
using Semantico.Core;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.Connector.AzureSynapse;

public static class ServiceCollectionExtensions
{
    public static SemanticoBuilder AddAzureSynapseConnector(this SemanticoBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.AzureSynapse, cs => new SqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.AzureSynapse, "Azure Synapse");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, AzureSynapseMetadataExtractor>();
        return builder;
    }
}
