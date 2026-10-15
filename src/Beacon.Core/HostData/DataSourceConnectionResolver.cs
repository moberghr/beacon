using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Configuration;

namespace Beacon.Core.HostData;

/// <summary>
/// The one place a database data source's stored connection data becomes a live connection string. Ordinary
/// data sources decrypt their stored string; host-managed ones decrypt a REFERENCE
/// (<c>{"hostConnectionStringName":"BeaconReadOnly"}</c>) and resolve the real string from the host's
/// <see cref="IConfiguration"/> at call time, so no host secret is ever persisted (§1.1). A migration write target is
/// refused here (<see cref="MigrationWriteTargets"/>): its login may write, and only the migration writer connects with it.
/// The resolved string then passes <see cref="IDataSourceTransportPolicy.EnsureStoredConnectionAllowed"/>: a connection
/// that does not authenticate the server over TLS is warned about once per data source, or refused.
/// </summary>
public interface IDataSourceConnectionResolver
{
    string GetConnectionString(DataSource dataSource);

    /// <summary>
    /// Whether the data source's connection (a host-managed source's configured string) authenticates the server over TLS,
    /// without connecting, warning or refusing: the data-source pages' badge. Throws when the connection data cannot be
    /// read (decrypted, or found in the host's configuration).
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
        MigrationWriteTargets.EnsureReadable(dataSource);

        var connectionString = Resolve(dataSource);
        transportPolicy.EnsureStoredConnectionAllowed(dataSource, connectionString);

        return connectionString;
    }

    public DataSourceTransportVerdict InspectTransport(DataSource dataSource)
    {
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
