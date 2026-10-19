using Microsoft.Data.SqlClient;
using Beacon.Core.Services.Providers;

namespace Beacon.Shared.SqlClient;

/// <summary>
/// Reads the transport settings of a SqlClient connection string with SqlClient's own builder, for the connectors'
/// <see cref="SqlClientTransportInspector"/>s. Compiled into each SqlClient connector as a linked file.
/// </summary>
internal static class SqlClientConnectionStringReader
{
    // Throws when the builder cannot read the connection string; the transport policy refuses it as unreadable.
    public static SqlClientTransportSettings Read(string connectionData)
    {
        var builder = new SqlConnectionStringBuilder(connectionData);

        return new SqlClientTransportSettings(EncryptOf(builder.Encrypt), builder.TrustServerCertificate, builder.DataSource, builder.InitialCatalog);
    }

    // Anything but Mandatory or Strict, including a mode a later driver adds, is Optional and fails the check.
    private static SqlClientEncrypt EncryptOf(SqlConnectionEncryptOption encrypt)
    {
        if (SqlConnectionEncryptOption.Strict.Equals(encrypt))
        {
            return SqlClientEncrypt.Strict;
        }

        return SqlConnectionEncryptOption.Mandatory.Equals(encrypt) ? SqlClientEncrypt.Mandatory : SqlClientEncrypt.Optional;
    }
}
