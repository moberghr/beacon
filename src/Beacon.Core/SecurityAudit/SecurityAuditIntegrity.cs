using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The keyed HMAC-SHA256 every security audit event carries over its fields, and the keys it is verified with. Events are
/// written under the current key: <c>Beacon:Audit:IntegrityKey</c> when set, else a key derived for this purpose only (HMAC
/// of a fixed label) from the secret the MCP caller hashes are derived from (<c>Beacon:Mcp:CallerHashKey</c>, else
/// <c>Beacon:EncryptionKey</c>); no secret is used as the HMAC key itself. Each event stores the id of its key
/// (<see cref="SecurityAuditEvent.KeyId"/>, a digest that reveals nothing of the key), and verification accepts the
/// current key, <c>Beacon:Audit:PreviousIntegrityKeys</c>, and the keys derived from the current secret and from each
/// <c>Beacon:PreviousEncryptionKeys</c> entry, so events keep verifying after a key change. It finds an event whose fields
/// were changed or that was written without the key; it cannot tell that an event was deleted.
/// </summary>
public sealed class SecurityAuditIntegrity
{
    private const string KeyDerivationLabel = "beacon:security-audit-integrity:v1";
    private const string KeyIdLabel = "beacon:security-audit-key-id:v1";
    private const string DigestKeyLabel = "beacon:security-audit-value-digest:v1";
    private const string CanonicalVersion = "v1";

    // What a digest of an absent value is computed over: a byte UTF-8 never produces, so no text digests the same.
    private static readonly byte[] AbsentValue = [0xFF];

    private readonly byte[] _currentKey;
    private readonly byte[] _digestKey;
    private readonly Dictionary<string, byte[]> _verificationKeys;

    /// <param name="currentSecret">The secret the current key is derived from.</param>
    /// <param name="previousSecrets">The secrets earlier keys were derived from; accepted when verifying.</param>
    /// <param name="keySource">Which setting <paramref name="currentSecret"/> came from (<see cref="SecurityAuditKeySources"/>).</param>
    public SecurityAuditIntegrity(string currentSecret, IEnumerable<string> previousSecrets, string keySource = SecurityAuditKeySources.IntegrityKey)
    {
        if (string.IsNullOrWhiteSpace(currentSecret))
        {
            throw new ArgumentException("The security audit integrity secret cannot be empty.", nameof(currentSecret));
        }

        _currentKey = Derive(currentSecret);
        _digestKey = HMACSHA256.HashData(_currentKey, Encoding.UTF8.GetBytes(DigestKeyLabel));
        CurrentKeyId = KeyIdOf(_currentKey);
        KeySource = keySource;
        _verificationKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [CurrentKeyId] = _currentKey };
        foreach (var secret in previousSecrets.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var key = Derive(secret);
            _verificationKeys.TryAdd(KeyIdOf(key), key);
        }
    }

    /// <summary>The id of the key new events are written under.</summary>
    public string CurrentKeyId { get; }

    /// <summary>
    /// Which setting the current key comes from (<see cref="SecurityAuditKeySources"/>): the integrity key itself, or a key
    /// derived from the caller-hash key or the encryption key, which changes when that key does.
    /// </summary>
    public string KeySource { get; }

    /// <summary>
    /// The integrity keys of a deployment, from its configuration (see the class). A configured key that is too short or a
    /// placeholder fails startup; the message never contains a key.
    /// </summary>
    public static SecurityAuditIntegrity From(IConfiguration configuration, string encryptionKey, IEnumerable<string> previousEncryptionKeys)
    {
        var integrityKey = configuration[$"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.IntegrityKey)}"];
        var problem = SecurityAuditOptionsValidator.IntegrityKeyProblem(integrityKey, nameof(SecurityAuditOptions.IntegrityKey));
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }

        var derivedSource = McpCallerSubjectHasher.KeySource(configuration, encryptionKey);
        var previousSection = configuration.GetSection($"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.PreviousIntegrityKeys)}");
        var previousIntegrityKeys = previousSection.Get<string[]>() ?? [];
        if (previousIntegrityKeys.Length == 0 && !string.IsNullOrWhiteSpace(previousSection.Value))
        {
            previousIntegrityKeys = [previousSection.Value];
        }

        var current = string.IsNullOrWhiteSpace(integrityKey) ? derivedSource : integrityKey;
        var previous = previousIntegrityKeys
            .Append(derivedSource)
            .Append(encryptionKey)
            .Concat(previousEncryptionKeys);
        var keySource = !string.IsNullOrWhiteSpace(integrityKey)
            ? SecurityAuditKeySources.IntegrityKey
            : string.Equals(derivedSource, encryptionKey, StringComparison.Ordinal)
                ? SecurityAuditKeySources.DerivedFromEncryptionKey
                : SecurityAuditKeySources.DerivedFromCallerHashKey;

        return new SecurityAuditIntegrity(current, previous, keySource);
    }

    /// <summary>Writes the current key's id and the event's HMAC onto <paramref name="auditEvent"/>.</summary>
    public void Seal(SecurityAuditEvent auditEvent)
    {
        auditEvent.KeyId = CurrentKeyId;
        auditEvent.Hmac = Compute(_currentKey, auditEvent);
    }

    /// <summary>How <paramref name="auditEvent"/> verifies: intact, changed (or forged), or under a key Beacon does not hold.</summary>
    public SecurityAuditVerdict Verify(SecurityAuditEvent auditEvent)
    {
        if (!_verificationKeys.TryGetValue(auditEvent.KeyId, out var key))
        {
            return SecurityAuditVerdict.UnknownKey;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(key, auditEvent));
        var stored = Encoding.ASCII.GetBytes(auditEvent.Hmac ?? string.Empty);

        return CryptographicOperations.FixedTimeEquals(expected, stored)
            ? SecurityAuditVerdict.Intact
            : SecurityAuditVerdict.Modified;
    }

    /// <summary>
    /// A keyed digest of <paramref name="value"/> (32 lowercase hex characters) for an event that must show a value changed
    /// without holding it (a setting's value before and after a save): the same value gives the same digest under the same
    /// key, an absent value one no text gives, and the value cannot be read back from it.
    /// </summary>
    public string Digest(string? value)
    {
        var bytes = value == null ? AbsentValue : Encoding.UTF8.GetBytes(value);

        return Convert.ToHexStringLower(HMACSHA256.HashData(_digestKey, bytes))[..32];
    }

    /// <summary>
    /// The time an event is stored with: whole microseconds, the precision both providers keep, so the HMAC computed now
    /// matches the one computed over the row read back.
    /// </summary>
    public static DateTime StoredTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    // Every field but the database id, in a fixed order, as a JSON array: unambiguous whatever a field holds.
    private static string Compute(byte[] key, SecurityAuditEvent auditEvent)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            CanonicalVersion,
            auditEvent.EventGuid.ToString("D"),
            auditEvent.CreatedTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            auditEvent.Category,
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.ActorKind,
            auditEvent.ActorUserId,
            auditEvent.ApiKeyId,
            auditEvent.CallerHash,
            auditEvent.ProjectId,
            auditEvent.ResourceType,
            auditEvent.ResourceId,
            auditEvent.DetailsJson,
            auditEvent.TraceId,
            auditEvent.KeyId
        });

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, canonical));
    }

    private static byte[] Derive(string secret)
    {
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(KeyDerivationLabel));
    }

    private static string KeyIdOf(byte[] key)
    {
        return "k1-" + Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(KeyIdLabel)))[..16];
    }
}

/// <summary>How a stored security audit event verifies.</summary>
public enum SecurityAuditVerdict
{
    Intact,

    /// <summary>The fields do not match the HMAC: the row was changed, or written without the key.</summary>
    Modified,

    /// <summary>The row names a key Beacon does not hold (a key no longer configured, or a forged id).</summary>
    UnknownKey
}

/// <summary>Where the security audit integrity key comes from (<see cref="SecurityAuditIntegrity.KeySource"/>).</summary>
public static class SecurityAuditKeySources
{
    /// <summary><c>Beacon:Audit:IntegrityKey</c>: the key does not change with any other setting.</summary>
    public const string IntegrityKey = "integrity_key";

    /// <summary>Derived from <c>Beacon:Mcp:CallerHashKey</c>: it changes when that key does.</summary>
    public const string DerivedFromCallerHashKey = "derived_from_caller_hash_key";

    /// <summary>Derived from <c>Beacon:EncryptionKey</c>: it changes when the encryption key does.</summary>
    public const string DerivedFromEncryptionKey = "derived_from_encryption_key";
}
