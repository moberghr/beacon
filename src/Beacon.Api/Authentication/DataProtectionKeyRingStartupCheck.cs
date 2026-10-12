using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Api.Authentication;

/// <summary>
/// Checks the Data Protection key ring when the host starts, outside Development:
/// <list type="bullet">
/// <item>No key repository configured: a Warning. The framework's default storage then applies (a directory of the
/// machine, or memory), so sign-in cookies may end at every restart or not be shared between instances, and the keys may
/// be stored without encryption.</item>
/// <item>A repository acknowledged as unencrypted (<see cref="BeaconDataProtectionExtensions.AllowUnencryptedKeysKey"/>):
/// a Warning, nothing else.</item>
/// <item>A repository with key encryption: every key that is not revoked and is stored without encryption (written before
/// key encryption was turned on, or by a Development process pointed at the same store) is revoked and a new, encrypted
/// key is created, so a cookie protected by a key anyone with read access to the store could copy is no longer accepted.
/// Sessions those keys protected end once; everyone signs in again. When the host does not generate keys itself
/// (<see cref="KeyManagementOptions.AutoGenerateKeys"/> off) or the repository refuses the write, startup fails with what to
/// do instead.</item>
/// </list>
/// Logs key ids and counts only, never key material.
/// </summary>
internal sealed class DataProtectionKeyRingStartupCheck(
    ILogger<DataProtectionKeyRingStartupCheck> logger,
    IHostEnvironment? environment = null,
    IConfiguration? configuration = null) : IStartupFilter
{
    // The element a key descriptor's secret is replaced by when it is encrypted at rest.
    private const string EncryptedSecretElement = "encryptedSecret";

    internal const string RevocationReason = "Stored without encryption at rest; revoked by Beacon at startup.";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            Inspect(app.ApplicationServices);
            next(app);
        };
    }

    internal void Inspect(IServiceProvider services)
    {
        if (environment?.IsDevelopment() == true)
        {
            return;
        }

        var options = services.GetService<IOptions<KeyManagementOptions>>()?.Value;
        if (options == null)
        {
            return;
        }

        if (options.XmlRepository == null)
        {
            logger.LogWarning(
                ProjectAccessEvents.DataProtectionKeyRing,
                "No Data Protection key repository is configured, so the framework's default storage holds the keys that protect " +
                "Beacon's sign-in cookies: sessions may end at every restart or not carry over between instances, and the keys " +
                "may be stored without encryption. Call AddBeaconDataProtection (with Beacon:DataProtection:ApplicationName " +
                "and a ProtectKeysWith* call) or configure Data Protection in the host");

            return;
        }

        if (options.XmlEncryptor is null or NullXmlEncryptor)
        {
            if (BeaconDataProtectionExtensions.AllowsUnencryptedKeys(configuration))
            {
                logger.LogWarning(
                    ProjectAccessEvents.DataProtectionKeyRing,
                    "Data Protection keys are persisted without encryption at rest, as {Setting} acknowledges: whoever can " +
                    "read the key repository can issue sign-in cookies",
                    BeaconDataProtectionExtensions.AllowUnencryptedKeysKey);
            }

            return;
        }

        RevokeUnencryptedKeys(services, options);
    }

    private void RevokeUnencryptedKeys(IServiceProvider services, KeyManagementOptions options)
    {
        var keyManager = services.GetRequiredService<IKeyManager>();
        var encrypted = options.XmlRepository!.GetAllElements()
            .Where(x => x.Name.LocalName == "key")
            .Where(x => x.Descendants().Any(y => y.Name.LocalName == EncryptedSecretElement))
            .Select(KeyIdOf)
            .OfType<Guid>()
            .ToHashSet();
        var unencrypted = keyManager.GetAllKeys()
            .Where(x => !x.IsRevoked)
            .Where(x => !encrypted.Contains(x.KeyId))
            .Select(x => x.KeyId)
            .ToList();
        if (unencrypted.Count == 0)
        {
            return;
        }

        var keyIds = string.Join(", ", unencrypted);
        if (!options.AutoGenerateKeys)
        {
            throw new InvalidOperationException(
                $"The Data Protection key ring holds {unencrypted.Count} key(s) stored without encryption at rest ({keyIds}), " +
                "and this host does not generate keys (AutoGenerateKeys is off), so Beacon does not revoke them. Revoke them " +
                "where the key ring is managed (IKeyManager.RevokeKey, or delete them from the repository) and create a new " +
                "key there, then start again.");
        }

        Guid created;
        try
        {
            foreach (var keyId in unencrypted)
            {
                keyManager.RevokeKey(keyId, RevocationReason);
            }

            var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            created = keyManager.CreateNewKey(now, now + options.NewKeyLifetime).KeyId;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The Data Protection key ring holds {unencrypted.Count} key(s) stored without encryption at rest ({keyIds}), " +
                "and Beacon could not revoke them and create a new key. Make the key repository writable for this host, or " +
                "revoke those keys (IKeyManager.RevokeKey, or delete them from the repository) and start again.",
                ex);
        }

        logger.LogWarning(
            ProjectAccessEvents.DataProtectionKeyRing,
            "Revoked {Count} Data Protection key(s) stored without encryption at rest ({KeyIds}) and created key {NewKeyId}: " +
            "the sign-in sessions they protected have ended, and those users sign in again",
            unencrypted.Count,
            keyIds,
            created);
    }

    private static Guid? KeyIdOf(XElement element)
    {
        return Guid.TryParse((string?)element.Attribute("id"), out var id) ? id : null;
    }
}
