using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Beacon.Shared.SqlClient;

namespace Beacon.Connector.SqlServer;

/// <summary>The SQL Server driver options Beacon does not accept, read with SqlClient's own builder (<see cref="SqlClientConnectionOptionReader"/>).</summary>
public sealed class SqlServerConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.MSSQL;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        return SqlClientConnectionOptionReader.Inspect(connectionData);
    }
}
