using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>
/// The one place a database data source's stored connection data becomes a live connection string. Ordinary
/// data sources decrypt their stored string; host-managed ones decrypt a REFERENCE
/// (<c>{"hostConnectionStringName":"BeaconReadOnly"}</c>) and resolve the real string from the host's
/// <see cref="IConfiguration"/> at call time, so no host secret is ever persisted (§1.1). A disabled source
/// (<see cref="DataSource.DisabledTime"/>) is refused first on every path, with a <see cref="DataSourceDisabledException"/>,
/// before anything is decrypted or its transport checked. While the change approval workflow covers data-source connections
/// (<see cref="ApprovalWorkflowOptions.CoversDataSourceConnections"/>), a governed source
/// (<see cref="DataSource.ApprovedTargetHash"/> set) connects only with its approved definition: a created source waiting
/// for its first approval is refused before anything is decrypted, a changed one once its connection data, decrypted once,
/// is not the approved definition; both with a <see cref="DataSourceAwaitingApprovalException"/>. A migration write target is
/// refused here (<see cref="MigrationWriteTargets"/>): its login may write, and only the migration writer connects with it.
/// The resolved string then passes <see cref="IDataSourceTransportPolicy.EnsureStoredConnectionAllowed"/>: a connection
/// that does not authenticate the server over TLS is warned about once per data source, or refused. Every connector and
/// service that connects with a data source's stored connection data goes through this resolver; nothing else decrypts it.
/// A host that registers its own implementation before Beacon's (registration is <c>TryAdd</c>) takes over that duty: the
/// approval hold on data-source connections is enforced only here, so its resolver must refuse a source awaiting approval
/// itself (<see cref="DataSourceAwaitingApprovalException"/>), or the hold is not enforced. The API host warns at startup
/// when the hold is on and the resolver is not Beacon's.
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

/// <summary>
/// Reads the definition of a data source's stored connection (<see cref="DataSourceApprovalDefinition"/>): the only place
/// a stored connection becomes a definition hash, so the connection resolver remains the one decrypt site. Implemented by
/// the connection resolver.
/// </summary>
internal interface IDataSourceDefinitionReader
{
    /// <summary>
    /// The keyed hash, under the current key, of the definition of <paramref name="dataSource"/>'s stored connection (the
    /// full entity); null when the stored connection cannot be read (it cannot be decrypted, or a host reference names no
    /// configured string), which counts as a changed definition.
    /// </summary>
    string? CurrentDefinitionHash(DataSource dataSource);

    /// <summary>
    /// True when <paramref name="hash"/> is the keyed hash, under the current key or a previous one, of the definition of
    /// <paramref name="dataSource"/>'s stored connection; false when it is null or held, or the connection cannot be read.
    /// </summary>
    bool DefinitionMatches(DataSource dataSource, string? hash);
}

internal sealed class DataSourceConnectionResolver(
    IEncryptionService encryptionService,
    IConfiguration configuration,
    IDataSourceTransportPolicy transportPolicy,
    BeaconConfiguration beaconConfiguration,
    ConnectionDefinitionHasher definitionHasher,
    ILogger<DataSourceConnectionResolver>? logger = null) : IDataSourceConnectionResolver, IDataSourceDefinitionReader
{
    // The sources whose stored connection was found unreadable, each logged once per process.
    private static readonly ConcurrentDictionary<int, byte> UnreadableLogged = new();

    public string GetConnectionString(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        var approvedHash = EnsureApproved(dataSource);
        MigrationWriteTargets.EnsureReadable(dataSource);

        return Checked(dataSource, approvedHash);
    }

    public string GetConnectionData(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        var approvedHash = EnsureApproved(dataSource);
        MigrationWriteTargets.EnsureReadable(dataSource);

        return Checked(dataSource, approvedHash);
    }

    public string GetWriteConnectionString(DataSource dataSource)
    {
        EnsureEnabled(dataSource);
        var approvedHash = EnsureApproved(dataSource);
        var refusal = MigrationWriteTargets.DestinationRefusal(dataSource);
        if (refusal != null)
        {
            throw new InvalidOperationException(refusal);
        }

        return Checked(dataSource, approvedHash);
    }

    public string? CurrentDefinitionHash(DataSource dataSource)
    {
        var canonical = StoredCanonical(dataSource);

        return canonical == null ? null : definitionHasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, canonical);
    }

    public bool DefinitionMatches(DataSource dataSource, string? hash)
    {
        if (hash == null || DataSourceApprovalDefinition.IsHeld(hash))
        {
            return false;
        }

        var canonical = StoredCanonical(dataSource);

        return canonical != null && definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, hash, canonical);
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

    // Right after the enabled check: a source the approval workflow governs (covered, and an approved hash recorded; never a
    // host-managed one) returns the hash its connection must match. A created source waiting for its first approval is
    // refused here, before its connection data is decrypted; a source not governed returns null and connects as before.
    private string? EnsureApproved(DataSource dataSource)
    {
        if (dataSource.ApprovedTargetHash == null
            || dataSource.HostManagedKey != null
            || !beaconConfiguration.ApprovalWorkflow.Covers(ChangeApprovalKind.DataSourceConnection))
        {
            return null;
        }

        if (DataSourceApprovalDefinition.IsHeld(dataSource.ApprovedTargetHash))
        {
            throw new DataSourceAwaitingApprovalException(dataSource.Id);
        }

        return dataSource.ApprovedTargetHash;
    }

    // The connection data, decrypted once: a governed source's definition must be its approved one (a change of its secrets
    // alone keeps the definition), then the transport check applies.
    private string Checked(DataSource dataSource, string? approvedHash)
    {
        var connectionData = Resolve(dataSource);
        if (approvedHash != null
            && !DataSourceApprovalDefinition.Allows(definitionHasher, approvedHash, Canonical(dataSource, connectionData)))
        {
            throw new DataSourceAwaitingApprovalException(dataSource.Id);
        }

        transportPolicy.EnsureStoredConnectionAllowed(dataSource, connectionData);

        return connectionData;
    }

    private string Canonical(DataSource dataSource, string connectionData)
    {
        return DataSourceApprovalDefinition.Canonical(transportPolicy, dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionData);
    }

    // The canonical definition of the stored connection, or null when it cannot be read: not decrypted (a key that is gone,
    // a corrupted value) or a host reference that names no configured string. Never the reason, which can quote the value.
    private string? StoredCanonical(DataSource dataSource)
    {
        string connectionData;
        try
        {
            connectionData = Resolve(dataSource);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            // Counts as a changed definition; the type only, as a message can quote the connection (§1.11).
            if (UnreadableLogged.TryAdd(dataSource.Id, 0))
            {
                logger?.LogWarning(
                    "The stored connection of data source {DataSourceId} cannot be read ({ExceptionType}): it counts as a changed definition until it is entered again",
                    dataSource.Id,
                    ex.GetType().Name);
            }

            return null;
        }

        return Canonical(dataSource, connectionData);
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
