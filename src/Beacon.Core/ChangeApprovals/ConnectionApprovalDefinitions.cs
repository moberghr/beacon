using System.Buffers.Text;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.Settings;
using Beacon.Core.Notifications;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.ChangeApprovals;

/// <summary>What a <see cref="ConnectionDefinitionHasher"/> hashes: each purpose has keys of its own.</summary>
internal enum ConnectionDefinitionPurpose
{
    /// <summary>A data source's connection definition (<see cref="DataSourceApprovalDefinition"/>).</summary>
    DataSourceDefinition = 1,

    /// <summary>The stored LLM connection (<see cref="LlmConnectionDefinition"/>).</summary>
    LlmConnection = 2
}

/// <summary>
/// Keyed hashes of a data source's connection definition and of the LLM connection: an HMAC-SHA256 under a key derived for
/// its one purpose (HKDF-SHA256 with its own label) from <c>Beacon:EncryptionKey</c>, so someone who reads a stored hash of
/// host or user names cannot test guesses against it. A stored hash carries <see cref="Prefix"/>. A hash or fingerprint
/// written under a key now listed in <c>Beacon:PreviousEncryptionKeys</c> still verifies: verification tries the current
/// key, then each previous one. Fingerprints are short keyed digests under other derived keys, shown so two people can
/// compare a value out of band without either seeing it; they reveal nothing without the key.
/// </summary>
internal sealed class ConnectionDefinitionHasher
{
    /// <summary>Marks a keyed hash.</summary>
    public const string Prefix = "v1:";

    /// <summary>How many hex characters a fingerprint shows.</summary>
    public const int FingerprintLength = 12;

    private const string DataSourceDefinitionLabel = "beacon:data-source-definition:v1";
    private const string LlmConnectionLabel = "beacon:llm-connection:v1";
    private const string DataSourceFingerprintLabel = "beacon:data-source-definition-fingerprint:v1";
    private const string LlmConnectionFingerprintLabel = "beacon:llm-connection-fingerprint:v1";

    private readonly PurposeKeys _current;
    private readonly IReadOnlyList<PurposeKeys> _previous;

    /// <param name="encryptionKey">The key new hashes are written under: <c>Beacon:EncryptionKey</c>.</param>
    /// <param name="previousKeys">The keys hashes written earlier may be under: <c>Beacon:PreviousEncryptionKeys</c> (verified, never written).</param>
    public ConnectionDefinitionHasher(string encryptionKey, IEnumerable<string>? previousKeys = null)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        _current = PurposeKeys.Derive(encryptionKey);
        _previous = (previousKeys ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !string.Equals(x, encryptionKey, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(PurposeKeys.Derive)
            .ToList();
    }

    /// <summary>True when a previous key is configured, so a stored hash or fingerprint may have been written under one.</summary>
    public bool HasPreviousKeys => _previous.Count > 0;

    /// <summary>The keyed hash of <paramref name="canonical"/> under the current key, as it is stored.</summary>
    public string Hash(ConnectionDefinitionPurpose purpose, string canonical)
    {
        return Under(_current.Hash(purpose), canonical);
    }

    /// <summary>
    /// True when <paramref name="stored"/> is the keyed hash of <paramref name="canonical"/> under the current key or a
    /// previous one. A missing, held or malformed value matches nothing.
    /// </summary>
    public bool Matches(ConnectionDefinitionPurpose purpose, string? stored, string canonical)
    {
        if (stored == null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Same(stored, Under(_current.Hash(purpose), canonical))
            || _previous.Any(x => Same(stored, Under(x.Hash(purpose), canonical)));
    }

    /// <summary>True when <paramref name="stored"/> is the keyed hash of <paramref name="canonical"/> under the current key.</summary>
    public bool IsCurrent(ConnectionDefinitionPurpose purpose, string? stored, string canonical)
    {
        return stored != null && Same(stored, Under(_current.Hash(purpose), canonical));
    }

    /// <summary>A short keyed fingerprint of <paramref name="value"/> (lowercase hex), for comparing it out of band.</summary>
    public string Fingerprint(ConnectionDefinitionPurpose purpose, string value)
    {
        return FingerprintUnder(_current.Fingerprint(purpose), value);
    }

    /// <summary>True when <paramref name="fingerprint"/> is the fingerprint of <paramref name="value"/> under the current key or a previous one.</summary>
    public bool FingerprintMatches(ConnectionDefinitionPurpose purpose, string? fingerprint, string value)
    {
        return fingerprint != null
            && (Same(fingerprint, FingerprintUnder(_current.Fingerprint(purpose), value))
                || _previous.Any(x => Same(fingerprint, FingerprintUnder(x.Fingerprint(purpose), value))));
    }

    /// <summary>True when <paramref name="fingerprint"/> is the fingerprint of <paramref name="value"/> under the current key.</summary>
    public bool IsCurrentFingerprint(ConnectionDefinitionPurpose purpose, string? fingerprint, string value)
    {
        return fingerprint != null && Same(fingerprint, FingerprintUnder(_current.Fingerprint(purpose), value));
    }

    // Base64url keeps the stored value within the request's 64-character definition hash column.
    private static string Under(byte[] key, string canonical)
    {
        return Prefix + Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    private static string FingerprintUnder(byte[] key, string value)
    {
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)))[..FingerprintLength];
    }

    private static bool Same(string first, string second)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }

    // The keys one encryption key gives: a hash key and a fingerprint key per purpose.
    private sealed record PurposeKeys(byte[] DataSourceHash, byte[] LlmConnectionHash, byte[] DataSourceFingerprint, byte[] LlmConnectionFingerprint)
    {
        public static PurposeKeys Derive(string key)
        {
            return new PurposeKeys(
                DeriveOne(key, DataSourceDefinitionLabel),
                DeriveOne(key, LlmConnectionLabel),
                DeriveOne(key, DataSourceFingerprintLabel),
                DeriveOne(key, LlmConnectionFingerprintLabel));
        }

        public byte[] Hash(ConnectionDefinitionPurpose purpose)
        {
            return purpose == ConnectionDefinitionPurpose.LlmConnection ? LlmConnectionHash : DataSourceHash;
        }

        public byte[] Fingerprint(ConnectionDefinitionPurpose purpose)
        {
            return purpose == ConnectionDefinitionPurpose.LlmConnection ? LlmConnectionFingerprint : DataSourceFingerprint;
        }

        private static byte[] DeriveOne(string key, string label)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key), 32, [], Encoding.UTF8.GetBytes(label));
        }
    }
}

/// <summary>
/// A data source's connection as its driver's builder normalizes it, without its secrets: what an approval of the
/// connection covers. <see cref="Settings"/> holds the builder's canonical key names (so synonyms such as <c>Server</c> and
/// <c>Host</c> name one setting) with their values, ordinally sorted, settings left unset left out, and the engine's secret
/// keys removed; a key the builder does not know is kept, so it is governed too. <see cref="Login"/> is the user name, or
/// the authentication mode when there is none. The values are identifiers (hosts, databases, user names, options) and are
/// never logged; only the keyed hash of the definition is stored.
/// </summary>
public sealed record DataSourceConnectionDefinition(IReadOnlyList<KeyValuePair<string, string>> Settings, string? Login)
{
    /// <summary>The number of settings only: the values are identifiers that are never logged.</summary>
    public override string ToString()
    {
        return $"{nameof(DataSourceConnectionDefinition)} {{ Settings = {Settings.Count} }}";
    }

    /// <summary>
    /// The definition of <paramref name="settings"/>: each setting with a value, those named by
    /// <paramref name="secretKeys"/> (any case) left out, ordinally sorted by name.
    /// </summary>
    public static DataSourceConnectionDefinition Create(
        IEnumerable<KeyValuePair<string, string?>> settings,
        IEnumerable<string> secretKeys,
        string? login)
    {
        var secrets = secretKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = settings
            .Where(x => !string.IsNullOrEmpty(x.Value))
            .Where(x => !secrets.Contains(x.Key))
            .Select(x => new KeyValuePair<string, string>(x.Key, x.Value!))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal)
            .ToList();

        return new DataSourceConnectionDefinition(kept, login);
    }

    /// <summary>
    /// The definition of a connection a driver's builder parsed: the settings the builder writes back (under its canonical
    /// names, only those that were set), without those set to the value <paramref name="defaults"/> (a builder of the same
    /// driver with nothing set) holds for them, so a default typed out is the default left out, and without
    /// <paramref name="secretKeys"/>.
    /// </summary>
    public static DataSourceConnectionDefinition FromBuilder(
        DbConnectionStringBuilder builder,
        DbConnectionStringBuilder defaults,
        IEnumerable<string> secretKeys,
        string? login)
    {
        var written = new DbConnectionStringBuilder { ConnectionString = builder.ConnectionString };
        var settings = written.Keys
            .Cast<string>()
            .Select(x => new KeyValuePair<string, string?>(CanonicalName(builder, x), Convert.ToString(written[x], CultureInfo.InvariantCulture)))
            .Where(x => !IsDefault(defaults, x.Key, x.Value));

        return Create(settings, secretKeys, login);
    }

    /// <summary>Two definitions are equal when they hold the same settings, in any order, and the same login.</summary>
    public bool Equals(DataSourceConnectionDefinition? other)
    {
        return other != null
            && string.Equals(Login, other.Login, StringComparison.Ordinal)
            && Sorted(Settings).SequenceEqual(Sorted(other.Settings));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Login, StringComparer.Ordinal);
        foreach (var setting in Sorted(Settings))
        {
            hash.Add(setting.Key, StringComparer.Ordinal);
            hash.Add(setting.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal static IEnumerable<KeyValuePair<string, string>> Sorted(IEnumerable<KeyValuePair<string, string>> settings)
    {
        return settings
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Value, StringComparer.Ordinal);
    }

    // The builder writes back the value it holds, formatted as it formats a default, so the two compare as written.
    private static bool IsDefault(DbConnectionStringBuilder defaults, string key, string? value)
    {
        return defaults.TryGetValue(key, out var unset)
            && unset != null
            && string.Equals(Convert.ToString(unset, CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    // The builder's own spelling of a key it wrote back (a plain builder may not keep the case it was written in).
    private static string CanonicalName(DbConnectionStringBuilder builder, string key)
    {
        return builder.Keys
            .Cast<string>()
            .Where(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault() ?? key;
    }
}

/// <summary>
/// What an approval of a data source's connection covers, and how it is checked: the keyed hash
/// (<see cref="ConnectionDefinitionHasher"/>) of its type, engine and connection definition
/// (<see cref="DataSourceConnectionDefinition"/>, secrets left out), or, when its connector gives no definition, of its
/// whole connection data, secrets included (every change of such a source, a rotation too, is governed). A stored
/// <see cref="Data.Entities.DataSource.ApprovedTargetHash"/> is null (not governed), <see cref="Held"/> (a created source
/// waiting for its first approval) or such a hash.
/// </summary>
internal static class DataSourceApprovalDefinition
{
    /// <summary>The approved hash of a created source waiting for its first approval: it connects to nothing.</summary>
    public const string Held = "none";

    /// <summary>True when <paramref name="approvedHash"/> holds a created source until its first approval.</summary>
    public static bool IsHeld(string? approvedHash)
    {
        return string.Equals(approvedHash, Held, StringComparison.Ordinal);
    }

    /// <summary>The text a data source's definition is hashed and fingerprinted from.</summary>
    public static string Canonical(DataSourceType type, DatabaseEngineType? engine, DataSourceConnectionDefinition? definition, string connectionData)
    {
        if (definition == null)
        {
            var whole = new
            {
                type = (int)type,
                engine = (int?)engine,
                connectionData
            };

            return "beacon:data-source-definition:v1:whole\n" + JsonSerializer.Serialize(whole);
        }

        var canonical = new
        {
            type = (int)type,
            engine = (int?)engine,
            settings = DataSourceConnectionDefinition.Sorted(definition.Settings)
                .Select(x => new[] { x.Key, x.Value })
                .ToList(),
            login = definition.Login
        };

        return "beacon:data-source-definition:v1\n" + JsonSerializer.Serialize(canonical);
    }

    /// <summary>
    /// The text the connection data of a source of <paramref name="type"/> and <paramref name="engine"/> is hashed from:
    /// its definition as the connector's inspector reads it (<see cref="DataSourceTransportVerdict.Definition"/>), or the
    /// whole connection data when the inspector gives none or cannot read it.
    /// </summary>
    public static string Canonical(IDataSourceTransportPolicy policy, DataSourceType type, DatabaseEngineType? engine, string connectionData)
    {
        return Canonical(type, engine, policy.Inspect(type, engine, connectionData).Definition, connectionData);
    }

    /// <summary>The keyed hash of a data source's definition, as it is stored and approved.</summary>
    public static string Hash(
        ConnectionDefinitionHasher hasher,
        DataSourceType type,
        DatabaseEngineType? engine,
        DataSourceConnectionDefinition? definition,
        string connectionData)
    {
        return hasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical(type, engine, definition, connectionData));
    }

    /// <summary>
    /// True when a source whose approved hash is <paramref name="approvedHash"/> may connect with the definition whose
    /// canonical text is <paramref name="canonical"/>: it is not governed (null), or the definition is the approved one,
    /// under the current key or a previous one. A held source, and a definition that cannot be read (null), never connect.
    /// </summary>
    public static bool Allows(ConnectionDefinitionHasher hasher, string? approvedHash, string? canonical)
    {
        return approvedHash == null
            || (canonical != null && hasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, approvedHash, canonical));
    }

    /// <summary>
    /// The names of the settings <paramref name="proposed"/> sets or changes against <paramref name="previous"/> (every
    /// setting when there is none), with <c>Login</c> when the login changed; ordinally sorted, never a value.
    /// </summary>
    public static IReadOnlyList<string> ChangedSettings(DataSourceConnectionDefinition? previous, DataSourceConnectionDefinition proposed)
    {
        var before = (previous?.Settings ?? [])
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Value).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var after = proposed.Settings
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Value).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var changed = before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .Where(x => !before.TryGetValue(x, out var was) || !after.TryGetValue(x, out var now) || !was.SequenceEqual(now, StringComparer.Ordinal))
            .ToList();
        if (previous != null && !string.Equals(previous.Login, proposed.Login, StringComparison.Ordinal))
        {
            changed.Add("Login");
        }

        return changed
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A data source's connection as a reviewer is shown it: the host, port, database and login values, the names of the
    /// settings the change sets or changes, and a keyed fingerprint of the definition. Without a definition only the
    /// fingerprint of the whole connection data is shown. Never a secret, never another setting's value.
    /// </summary>
    public static DataSourceConnectionView View(
        ConnectionDefinitionHasher hasher,
        string name,
        DataSourceType type,
        DatabaseEngineType? engine,
        DataSourceTransportTarget? target,
        DataSourceConnectionDefinition? definition,
        string connectionData,
        DataSourceConnectionDefinition? previous,
        bool authenticatesServer,
        bool databaseReadOnlyEnforced,
        bool isTargetChange)
    {
        return new DataSourceConnectionView
        {
            Name = name,
            DataSourceType = type,
            DatabaseEngineType = engine,
            Host = target?.Host,
            Port = target?.Port,
            Database = target?.Database,
            Login = definition?.Login,
            ChangedSettings = definition == null ? [] : ChangedSettings(previous, definition),
            SecretOnlyDefinition = definition == null,
            Fingerprint = hasher.Fingerprint(ConnectionDefinitionPurpose.DataSourceDefinition, Canonical(type, engine, definition, connectionData)),
            AuthenticatesServer = authenticatesServer,
            DatabaseReadOnlyEnforced = databaseReadOnlyEnforced,
            IsTargetChange = isTargetChange
        };
    }
}

/// <summary>
/// The stored LLM connection: the provider, endpoint, region and authentication mode an approval governs, the models, and
/// every credential. Its keyed hash binds all of it (a proposal names exactly the connection it applies); a reviewer sees
/// <see cref="View"/>, which shows the endpoint as scheme and host only and every credential as a fingerprint.
/// </summary>
internal sealed record LlmConnectionDefinition
{
    public const string ProviderField = "Provider";
    public const string EndpointField = "Endpoint";
    public const string RegionField = "Region";
    public const string AuthModeField = "Authentication mode";
    public const string ApiKeyField = "API key";
    public const string SessionTokenField = "Session token";
    public const string AwsAccessKeyIdField = "AWS access key id";
    public const string AwsSecretAccessKeyField = "AWS secret access key";

    public AiProvider? Provider { get; init; }

    public string? Endpoint { get; init; }

    public string? Region { get; init; }

    public BedrockAuthMode BedrockAuthMode { get; init; }

    public string? Model { get; init; }

    public string? FastModel { get; init; }

    public string? ApiKey { get; init; }

    public string? SessionToken { get; init; }

    public string? AwsAccessKeyId { get; init; }

    public string? AwsSecretAccessKey { get; init; }

    /// <summary>The provider only: the endpoint and credentials are never printed.</summary>
    public override string ToString()
    {
        return $"{nameof(LlmConnectionDefinition)} {{ Provider = {Provider} }}";
    }

    /// <summary>The connection the settings hold.</summary>
    public static LlmConnectionDefinition Of(AppSettingsData settings)
    {
        return new LlmConnectionDefinition
        {
            Provider = settings.LlmProvider,
            Endpoint = settings.LlmEndpoint,
            Region = settings.LlmRegion,
            BedrockAuthMode = settings.LlmBedrockAuthMode,
            Model = settings.LlmModel,
            FastModel = settings.LlmFastModel,
            ApiKey = settings.LlmApiKey,
            SessionToken = settings.LlmSessionToken,
            AwsAccessKeyId = settings.LlmAwsAccessKeyId,
            AwsSecretAccessKey = settings.LlmAwsSecretAccessKey
        };
    }

    /// <summary>
    /// The names of the governed parts (provider, endpoint, region, authentication mode) <paramref name="proposed"/>
    /// changes against <paramref name="previous"/>; every one when there is none (the host's configuration). Empty for a
    /// change of credentials or models alone.
    /// </summary>
    public static IReadOnlyList<string> GovernedChanges(LlmConnectionDefinition? previous, LlmConnectionDefinition proposed)
    {
        if (previous == null)
        {
            return [ProviderField, EndpointField, RegionField, AuthModeField];
        }

        var changed = new List<string>();
        if (previous.Provider != proposed.Provider)
        {
            changed.Add(ProviderField);
        }

        if (!string.Equals(previous.Endpoint, proposed.Endpoint, StringComparison.Ordinal))
        {
            changed.Add(EndpointField);
        }

        if (!string.Equals(previous.Region, proposed.Region, StringComparison.Ordinal))
        {
            changed.Add(RegionField);
        }

        if (previous.BedrockAuthMode != proposed.BedrockAuthMode)
        {
            changed.Add(AuthModeField);
        }

        return changed;
    }

    /// <summary>An endpoint as a reviewer is shown it: scheme, host and port; a mask when it is no absolute URL; null when there is none.</summary>
    public static string? EndpointShown(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return RecipientSecrets.Mask;
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";

        return $"{uri.Scheme}://{uri.IdnHost}{port}";
    }

    /// <summary>The connection a host configures (nothing stored yet), as a reviewer is shown it: no value.</summary>
    public static LlmConnectionView HostConfigurationView()
    {
        return new LlmConnectionView { FromHostConfiguration = true };
    }

    /// <summary>The text the connection is hashed and fingerprinted from: every part, credentials included.</summary>
    public string Canonical()
    {
        var canonical = new
        {
            provider = (int?)Provider,
            endpoint = Endpoint,
            region = Region,
            bedrockAuthMode = (int)BedrockAuthMode,
            model = Model,
            fastModel = FastModel,
            apiKey = ApiKey,
            sessionToken = SessionToken,
            awsAccessKeyId = AwsAccessKeyId,
            awsSecretAccessKey = AwsSecretAccessKey
        };

        return "beacon:llm-connection:v1\n" + JsonSerializer.Serialize(canonical);
    }

    /// <summary>The keyed hash of the whole connection.</summary>
    public string Hash(ConnectionDefinitionHasher hasher)
    {
        return hasher.Hash(ConnectionDefinitionPurpose.LlmConnection, Canonical());
    }

    /// <summary>The keyed fingerprint of the whole connection, as a change names the connection it is based on.</summary>
    public string Fingerprint(ConnectionDefinitionHasher hasher)
    {
        return hasher.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, "connection\n" + Canonical());
    }

    /// <summary>
    /// The connection as a reviewer is shown it, based on the connection whose fingerprint is <paramref name="basedOn"/>:
    /// the endpoint as scheme and host with a fingerprint of the whole endpoint, and a fingerprint of each credential set.
    /// </summary>
    public LlmConnectionView View(ConnectionDefinitionHasher hasher, string? basedOn)
    {
        return new LlmConnectionView
        {
            Provider = Provider,
            Endpoint = EndpointShown(Endpoint),
            EndpointFingerprint = string.IsNullOrEmpty(Endpoint) ? null : hasher.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, EndpointInput(Endpoint)),
            Region = Region,
            BedrockAuthMode = BedrockAuthMode,
            Model = Model,
            FastModel = FastModel,
            BasedOn = basedOn,
            Credentials = CredentialsSet()
                .Select(x =>
                    new LlmCredentialView
                    {
                        Name = x.Name,
                        Fingerprint = hasher.Fingerprint(ConnectionDefinitionPurpose.LlmConnection, CredentialInput(x.Name, x.Value))
                    })
                .ToList()
        };
    }

    /// <summary>
    /// True when <paramref name="view"/> shows exactly this connection: every value it shows is this connection's, and every
    /// fingerprint is this connection's endpoint's or credential's, under the current key or, unless
    /// <paramref name="currentKeyOnly"/>, a previous one. The view's <see cref="LlmConnectionView.BasedOn"/> is not compared.
    /// </summary>
    public bool IsShownBy(LlmConnectionView view, ConnectionDefinitionHasher hasher, bool currentKeyOnly)
    {
        if (view.FromHostConfiguration
            || view.Provider != Provider
            || !string.Equals(view.Endpoint, EndpointShown(Endpoint), StringComparison.Ordinal)
            || !string.Equals(view.Region, Region, StringComparison.Ordinal)
            || view.BedrockAuthMode != BedrockAuthMode
            || !string.Equals(view.Model, Model, StringComparison.Ordinal)
            || !string.Equals(view.FastModel, FastModel, StringComparison.Ordinal))
        {
            return false;
        }

        var endpointShown = string.IsNullOrEmpty(Endpoint)
            ? view.EndpointFingerprint == null
            : Fingerprinted(hasher, view.EndpointFingerprint, EndpointInput(Endpoint), currentKeyOnly);
        if (!endpointShown)
        {
            return false;
        }

        var credentials = CredentialsSet();
        if (view.Credentials.Count != credentials.Count)
        {
            return false;
        }

        return credentials.All(x => view.Credentials
            .Where(y => string.Equals(y.Name, x.Name, StringComparison.Ordinal))
            .Any(y => Fingerprinted(hasher, y.Fingerprint, CredentialInput(x.Name, x.Value), currentKeyOnly)));
    }

    /// <summary>The settings a save writes for this connection: the provider, endpoint, region, authentication mode, models and credentials.</summary>
    public AppSettingsData ToSettings()
    {
        return new AppSettingsData
        {
            LlmProvider = Provider,
            LlmEndpoint = Endpoint,
            LlmRegion = Region,
            LlmBedrockAuthMode = BedrockAuthMode,
            LlmModel = Model,
            LlmFastModel = FastModel,
            LlmApiKey = ApiKey,
            LlmSessionToken = SessionToken,
            LlmAwsAccessKeyId = AwsAccessKeyId,
            LlmAwsSecretAccessKey = AwsSecretAccessKey
        };
    }

    private static string EndpointInput(string endpoint)
    {
        return "endpoint\n" + endpoint;
    }

    private static string CredentialInput(string name, string value)
    {
        return "credential\n" + name + "\n" + value;
    }

    private static bool Fingerprinted(ConnectionDefinitionHasher hasher, string? fingerprint, string value, bool currentKeyOnly)
    {
        return currentKeyOnly
            ? hasher.IsCurrentFingerprint(ConnectionDefinitionPurpose.LlmConnection, fingerprint, value)
            : hasher.FingerprintMatches(ConnectionDefinitionPurpose.LlmConnection, fingerprint, value);
    }

    // The credentials that are set, by name.
    private List<(string Name, string Value)> CredentialsSet()
    {
        (string Name, string? Value)[] credentials =
        [
            (ApiKeyField, ApiKey),
            (SessionTokenField, SessionToken),
            (AwsAccessKeyIdField, AwsAccessKeyId),
            (AwsSecretAccessKeyField, AwsSecretAccessKey)
        ];

        return credentials
            .Where(x => !string.IsNullOrEmpty(x.Value))
            .Select(x => (x.Name, x.Value!))
            .ToList();
    }
}
