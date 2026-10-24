using Beacon.Core.ChangeApprovals;

namespace Beacon.Core.Services.Providers;

/// <summary>SqlClient's <c>Encrypt</c> option as the transport check reads it.</summary>
public enum SqlClientEncrypt
{
    // Optional, false, or any value that is neither Mandatory nor Strict: the server may not be authenticated.
    Optional,
    Mandatory,
    Strict
}

/// <summary>
/// What <see cref="SqlClientTransportInspector"/> needs from a SqlClient connection string: the encryption mode, whether the
/// server certificate is trusted unchecked, the server and database the connection targets, and the connection's
/// definition.
/// </summary>
public sealed record SqlClientTransportSettings(SqlClientEncrypt Encrypt, bool TrustServerCertificate, string? DataSource, string? InitialCatalog)
{
    /// <summary>The connection as SqlClient's builder normalizes it, without its password; null when the reader gives none.</summary>
    public DataSourceConnectionDefinition? Definition { get; init; }

    /// <summary>The encryption settings only: the server, database and definition are never printed.</summary>
    public override string ToString()
    {
        return $"{nameof(SqlClientTransportSettings)} {{ Encrypt = {Encrypt}, TrustServerCertificate = {TrustServerCertificate} }}";
    }
}
