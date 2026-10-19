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
/// server certificate is trusted unchecked, and the server and database the connection targets.
/// </summary>
public sealed record SqlClientTransportSettings(SqlClientEncrypt Encrypt, bool TrustServerCertificate, string? DataSource, string? InitialCatalog);
