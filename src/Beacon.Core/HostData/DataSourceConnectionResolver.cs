using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Configuration;

namespace Beacon.Core.HostData;

/// <summary>
/// The one place a database data source's stored connection data becomes a live connection string. Ordinary
/// data sources decrypt their stored string; host-managed ones decrypt a REFERENCE
/// (<c>{"hostConnectionStringName":"BeaconReadOnly"}</c>) and resolve the real string from the host's
/// <see cref="IConfiguration"/> at call time, so no host secret is ever persisted (§1.1). A disabled source
/// (<see cref="DataSource.DisabledTime"/>) is refused first on every path, with a <see cref="DataSourceDisabledException"/>,
/// before anything is decrypted or its transport checked. A migration write target is
/// refused here (<see cref="MigrationWriteTargets"/>): its login may write, and only the migration writer connects with it.
/// The resolved string then passes <see cref="IDataSourceTransportPolicy.EnsureStoredConnectionAllowed"/>: a connection
/// that does not authenticate the server over TLS is warned about once per data source, or refused. Every connector and
/// service that connects with a data source's stored connection data goes through this resolver; nothing else decrypts it.
/// </summary>
public interface IDataSourceConnectionResolver
{
    /// <summary>The connection string of a database data source Beacon reads.</summary>
    string GetConnectionString(DataSource dataSource);

    /// <summary>
    /// The decrypted connection settings of a connector configured with a JSON document (API, Databricks, BigQuery,
    /// CloudWatch), with the same checks as <see cref="GetConnectionString"/>; the connector parses the document itself.
    /// </summary>
    string GetConnectionData(DataSource dataSource);

    /// <summary>
    /// The connection string the migration writer connects to a migration write target with: the one connection made to
    /// a write target, so the read refusal does not apply, but the transport check does. Refused, before anything is
    /// decrypted, for a source that is not a write target (<see cref="MigrationWriteTargets.DestinationRefusal"/>).
    /// </summary>
    string GetWriteConnectionString(DataSource dataSource);

    /// <summary>
    /// Whether the data source's connection (a host-managed source's configured string) authenticates the server over TLS,
    /// without connecting, warning or refusing: the data-source pages' badge. Throws when the connection data cannot be
    /// read (decrypted, or found in the host's configuration), and for a migration write target, whose connection data is
    /// never decrypted for a reader.
    /// </summary>
    DataSourceTransportVerdict InspectTransport(DataSource dataSource);
}

internal sealed class DataSourceConnectionResolver(
    IEncryptionService encryptionService,
    IConfiguration configuration,
    IDataSourceTransportPolicy transportPolicy) : IDataSourceConnectionResolver
{
    public string GetConnectionString(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        MigrationWriteTargets.EnsureReadable(dataSource);

        return Checked(dataSource);
    }

    public string GetConnectionData(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        MigrationWriteTargets.EnsureReadable(dataSource);

        return Checked(dataSource);
    }

    public string GetWriteConnectionString(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        var refusal = MigrationWriteTargets.DestinationRefusal(dataSource);
        if (refusal != null)
        {
            throw new InvalidOperationException(refusal);
        }

        return Checked(dataSource);
    }

    public DataSourceTransportVerdict InspectTransport(DataSource dataSource)
    {
        MigrationWriteTargets.EnsureReadable(dataSource);

        return transportPolicy.Inspect(dataSource.DataSourceType, dataSource.DatabaseEngineType, Resolve(dataSource));
    }

    /// <summary>Reads <c>ConnectionStrings:{name}</c>; the message names the entry, never its value.</summary>
    public static string ResolveConfigured(IConfiguration configuration, string connectionStringName)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Host data source connection string 'ConnectionStrings:{connectionStringName}' is missing or empty.");
        }

        return connectionString;
    }

    // First on every connection path: a disabled source is refused before its connection data is decrypted or its transport
    // checked, so nothing connects with its credentials.
    private static void EnsureEnabled(DataSource dataSource)
    {
        if (dataSource.DisabledTime != null)
        {
            throw new DataSourceDisabledException(dataSource.Id);
        }
    }

    private string Checked(DataSource dataSource)
    {
        var connectionData = Resolve(dataSource);
        transportPolicy.EnsureStoredConnectionAllowed(dataSource, connectionData);

        return connectionData;
    }

    private string Resolve(DataSource dataSource)
    {
        var decrypted = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
        if (dataSource.HostManagedKey == null)
        {
            return decrypted;
        }

        var reference = HostConnectionReference.Parse(decrypted);

        return ResolveConfigured(configuration, reference.HostConnectionStringName);
    }
}

/// <summary>The JSON persisted (encrypted) in <c>DataSource.EncryptedConnectionData</c> for host-managed sources.</summary>
internal sealed record HostConnectionReference(
    [property: JsonPropertyName("hostConnectionStringName")] string HostConnectionStringName)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static HostConnectionReference Parse(string json)
    {
        HostConnectionReference? reference;
        try
        {
            reference = JsonSerializer.Deserialize<HostConnectionReference>(json);
        }
        catch (JsonException)
        {
            reference = null;
        }

        if (reference == null || string.IsNullOrWhiteSpace(reference.HostConnectionStringName))
        {
            // Never echo the payload: a corrupted row could hold a real connection string.
            throw new InvalidOperationException("Host-managed data source has an invalid connection reference.");
        }

        return reference;
    }
}
