using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Beacon.Shared.SqlClient;

namespace Beacon.Connector.AzureSynapse;

/// <summary>The Azure Synapse driver options Beacon does not accept, read with SqlClient's own builder (<see cref="SqlClientConnectionOptionReader"/>).</summary>
public sealed class AzureSynapseConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.AzureSynapse;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        return SqlClientConnectionOptionReader.Inspect(connectionData);
    }
}
