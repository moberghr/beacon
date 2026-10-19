using Beacon.Core.Services.Providers;
using Beacon.Shared.SqlClient;

namespace Beacon.Connector.AzureSynapse;

/// <summary>
/// The Azure Synapse connection check: <c>Encrypt</c> Mandatory or Strict and <c>TrustServerCertificate=false</c>, read
/// with SqlClient's own builder (<see cref="SqlClientTransportInspector"/>).
/// </summary>
public sealed class AzureSynapseTransportInspector() : SqlClientTransportInspector(Core.Data.Enums.DatabaseEngineType.AzureSynapse)
{
    protected override SqlClientTransportSettings Read(string connectionData)
    {
        return SqlClientConnectionStringReader.Read(connectionData);
    }
}
