using Beacon.Core.Services.Providers;

namespace Beacon.Connector.SqlServer;

/// <summary>
/// The SQL Server connection check: <c>Encrypt</c> Mandatory or Strict and <c>TrustServerCertificate=false</c>, read with
/// SqlClient's own builder (<see cref="SqlClientTransportInspector"/>).
/// </summary>
public sealed class SqlServerTransportInspector() : SqlClientTransportInspector(Core.Data.Enums.DatabaseEngineType.MSSQL);
