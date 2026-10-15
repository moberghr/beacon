using Microsoft.Data.SqlClient;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The inspector of an engine Beacon connects to with Microsoft.Data.SqlClient (SQL Server, Azure Synapse). Such a
/// connection authenticates the server only with <c>Encrypt</c> set to <c>Mandatory</c> (true, the default of
/// Microsoft.Data.SqlClient 6) or <c>Strict</c>, and <c>TrustServerCertificate=false</c> (the default):
/// <c>Encrypt=Optional</c> may not encrypt at all, and a trusted server certificate is never checked. A certificate from
/// a private CA is pinned with <c>HostNameInCertificate</c> or <c>ServerCertificate</c>. Read with SqlClient's own builder.
/// Each connector derives its own type, so <c>TryAddEnumerable</c> keeps one per connector.
/// </summary>
public abstract class SqlClientTransportInspector(DatabaseEngineType databaseEngineType) : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => databaseEngineType;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new SqlConnectionStringBuilder(connectionData);
        var target = Target(builder);

        var encrypt = builder.Encrypt;
        if (!SqlConnectionEncryptOption.Mandatory.Equals(encrypt) && !SqlConnectionEncryptOption.Strict.Equals(encrypt))
        {
            return DataSourceTransportVerdict.Fail("Encrypt", "Set Encrypt=Mandatory (or Strict), or leave Encrypt out", target);
        }

        return builder.TrustServerCertificate
            ? DataSourceTransportVerdict.Fail("TrustServerCertificate", "Set TrustServerCertificate=false, or leave it out (use HostNameInCertificate or ServerCertificate for a certificate from a private CA)", target)
            : DataSourceTransportVerdict.Pass(target);
    }

    // "tcp:host,port": the host and, when given, the port.
    private static DataSourceTransportTarget Target(SqlConnectionStringBuilder builder)
    {
        var server = builder.DataSource ?? string.Empty;
        if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            server = server[4..];
        }

        var parts = server.Split(',', 2);
        int? port = parts.Length == 2 && int.TryParse(parts[1].Trim(), out var parsed) ? parsed : null;

        return new DataSourceTransportTarget(parts[0].Trim(), port, builder.InitialCatalog);
    }
}
