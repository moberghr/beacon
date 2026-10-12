using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Beacon.Core.Data;

namespace Beacon.Api.Authentication;

/// <summary>
/// ASP.NET Core Data Protection protects the <c>Beacon.Auth</c> session cookie and antiforgery tokens. It belongs to the
/// host: <see cref="AuthServiceExtensions.AddBeaconCookieAuthentication"/> configures none of it (no application name, no
/// key storage), so a host keeps its own setup. A host that wants Beacon's default calls
/// <see cref="AddBeaconDataProtection"/>. Either way, outside Development the host fails to start when the key ring is
/// persisted without key encryption (<see cref="DataProtectionKeyEncryptionCheck"/>, unless
/// <see cref="AllowUnencryptedKeysKey"/> acknowledges it), and at startup every key of a persisted ring that is still
/// stored without encryption is revoked and replaced (<see cref="DataProtectionKeyRingStartupCheck"/>).
/// </summary>
public static class BeaconDataProtectionExtensions
{
    /// <summary>The configuration key of the Data Protection application name Beacon's default setup uses.</summary>
    public const string ApplicationNameKey = "Beacon:DataProtection:ApplicationName";

    /// <summary>The application name Beacon's default setup uses in Development when none is configured.</summary>
    public const string DevelopmentApplicationName = "Beacon";

    /// <summary>
    /// The configuration key that acknowledges a key ring persisted without encryption at rest outside Development (for
    /// example on a single machine whose key directory only the service account can read). Off by default; while on,
    /// startup logs a Warning instead of refusing, and keys stored without encryption are not revoked.
    /// </summary>
    public const string AllowUnencryptedKeysKey = "Beacon:DataProtection:AllowUnencryptedKeys";

    /// <summary>
    /// Beacon's default Data Protection setup: the key ring in the Beacon database (<c>data_protection_keys</c>), under the
    /// application name <c>Beacon:DataProtection:ApplicationName</c>. Outside Development the name is required (give each
    /// deployment its own), and the keys must be encrypted at rest: chain <c>ProtectKeysWithAzureKeyVault</c>,
    /// <c>ProtectKeysWithCertificate</c> or another <c>ProtectKeysWith*</c> call on the returned builder, with a key or
    /// certificate only that environment holds, or startup fails. A host that configures Data Protection itself does not
    /// call this.
    /// </summary>
    public static IDataProtectionBuilder AddBeaconDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var applicationName = configuration[ApplicationNameKey]?.Trim();
        if (string.IsNullOrEmpty(applicationName))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{ApplicationNameKey} must be set outside Development when Beacon configures Data Protection " +
                    "(AddBeaconDataProtection). Give each deployment its own name (for example beacon-production), so a " +
                    "sign-in cookie issued by one environment is never accepted by another.");
            }

            applicationName = DevelopmentApplicationName;
        }

        services.AddDataProtectionKeyEncryptionCheck();

        return services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<BeaconContext>();
    }

    /// <summary>
    /// Registers <see cref="DataProtectionKeyEncryptionCheck"/> and <see cref="DataProtectionKeyRingStartupCheck"/>, run
    /// when the host starts.
    /// </summary>
    internal static IServiceCollection AddDataProtectionKeyEncryptionCheck(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KeyManagementOptions>, DataProtectionKeyEncryptionCheck>());
        services.AddOptions<KeyManagementOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, DataProtectionKeyRingStartupCheck>());

        return services;
    }

    /// <summary>The host acknowledged a key ring persisted without encryption at rest (<see cref="AllowUnencryptedKeysKey"/>).</summary>
    internal static bool AllowsUnencryptedKeys(IConfiguration? configuration)
    {
        return bool.TryParse(configuration?[AllowUnencryptedKeysKey], out var allowed) && allowed;
    }
}

/// <summary>
/// Outside Development, refuses a Data Protection key ring that is persisted to a configured repository without key
/// encryption (no <c>IXmlEncryptor</c>, or the <see cref="NullXmlEncryptor"/>): anyone who can read the repository, or a
/// copy of it, could then issue sign-in cookies. <see cref="BeaconDataProtectionExtensions.AllowUnencryptedKeysKey"/>
/// acknowledges such a ring instead (startup then logs a Warning). With no repository configured, the framework's default
/// storage applies and is the host's choice (startup logs a Warning). Without a host environment (no host) it judges as
/// outside Development.
/// </summary>
internal sealed class DataProtectionKeyEncryptionCheck(IHostEnvironment? environment = null, IConfiguration? configuration = null)
    : IValidateOptions<KeyManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, KeyManagementOptions options)
    {
        if (environment?.IsDevelopment() == true
            || options.XmlRepository == null
            || options.XmlEncryptor is not (null or NullXmlEncryptor)
            || BeaconDataProtectionExtensions.AllowsUnencryptedKeys(configuration))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            "Data Protection keys (which protect Beacon's sign-in cookies) are persisted without encryption at rest. Outside " +
            "Development, call ProtectKeysWithAzureKeyVault, ProtectKeysWithCertificate or another ProtectKeysWith* method on " +
            "the Data Protection builder (for Beacon's default setup, on the builder AddBeaconDataProtection returns), with a " +
            "key or certificate only this environment holds. A host that protects the key store by other means sets " +
            $"{BeaconDataProtectionExtensions.AllowUnencryptedKeysKey} to true instead.");
    }
}
