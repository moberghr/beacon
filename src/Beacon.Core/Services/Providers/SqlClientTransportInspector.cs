using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The inspector of an engine Beacon connects to with the SqlClient driver (SQL Server, Azure Synapse). Such a
/// connection authenticates the server only with <c>Encrypt</c> set to <c>Mandatory</c> (true, the default of
/// SqlClient 6) or <c>Strict</c>, and <c>TrustServerCertificate=false</c> (the default):
/// <c>Encrypt=Optional</c> may not encrypt at all, and a trusted server certificate is never checked. A certificate from
/// a private CA is pinned with <c>HostNameInCertificate</c> or <c>ServerCertificate</c>. The verdict is decided here; the
/// connection string is read by the connector (<see cref="Read"/>) with SqlClient's own builder, so the transport check does
/// not reference the driver (Core still has one bulk-copy use of it until a later change). A connection string the
/// builder cannot read throws, and the transport policy refuses it as unreadable.
/// Each connector derives its own type, so <c>TryAddEnumerable</c> keeps one per connector.
/// </summary>
public abstract class SqlClientTransportInspector(DatabaseEngineType databaseEngineType) : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => databaseEngineType;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var settings = Read(connectionData);
        var target = Target(settings);

        if (settings.Encrypt is not (SqlClientEncrypt.Mandatory or SqlClientEncrypt.Strict))
        {
            return DataSourceTransportVerdict.Fail("Encrypt", "Set Encrypt=Mandatory (or Strict), or leave Encrypt out", target);
        }

        return settings.TrustServerCertificate
            ? DataSourceTransportVerdict.Fail("TrustServerCertificate", "Set TrustServerCertificate=false, or leave it out (use HostNameInCertificate or ServerCertificate for a certificate from a private CA)", target)
            : DataSourceTransportVerdict.Pass(target);
    }

    /// <summary>
    /// The transport settings of <paramref name="connectionData"/> as SqlClient's own connection string builder reads
    /// them; any <c>Encrypt</c> value other than Mandatory or Strict is <see cref="SqlClientEncrypt.Optional"/>. Throws when
    /// the builder cannot read the connection string.
    /// </summary>
    protected abstract SqlClientTransportSettings Read(string connectionData);

    // "tcp:host,port": the host and, when given, the port.
    private static DataSourceTransportTarget Target(SqlClientTransportSettings settings)
    {
        var server = settings.DataSource ?? string.Empty;
        if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            server = server[4..];
        }

        var parts = server.Split(',', 2);
        int? port = parts.Length == 2 && int.TryParse(parts[1].Trim(), out var parsed) ? parsed : null;

        return new DataSourceTransportTarget(parts[0].Trim(), port, settings.InitialCatalog);
    }
}
