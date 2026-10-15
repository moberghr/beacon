using Microsoft.Data.SqlClient;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Connector.AzureSynapse;

public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddAzureSynapseConnector(this BeaconBuilder builder)
    {
        DbConnectionFactory.Register(DatabaseEngineType.AzureSynapse, cs => new SqlConnection(cs));
        ConnectorRegistry.RegisterDatabaseEngine(DatabaseEngineType.AzureSynapse, "Azure Synapse");
        builder.Services.AddTransient<IDatabaseMetadataExtractor, AzureSynapseMetadataExtractor>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSourceTransportInspector, AzureSynapseTransportInspector>());
        return builder;
    }
}
