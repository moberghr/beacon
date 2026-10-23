using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Warns at startup about the keys stored values may still be encrypted with (<c>Beacon:PreviousEncryptionKeys</c>): when
/// the setting is a single value rather than a list (it is read as one key), and, after the host has started, off the
/// startup thread and best effort, how many stored values are still under a previous key, how many no configured key
/// decrypts and how many keyed recipient hashes cannot be rewritten under the current key
/// (<see cref="IStoredValueReEncryptionService.CountAsync"/>), so an operator knows whether the previous keys can be
/// removed; and, with or without a previous key, how many stored values are still in the legacy v1 format, so an operator
/// knows when <c>Beacon:Encryption:AllowLegacyCbc</c> can be turned off. Counts and configuration names only, never a key
/// (§1.11); a database that cannot be read is noted, never a failure.
/// </summary>
internal sealed class EncryptionKeyStartupWarnings(
    IConfiguration configuration,
    ILogger<EncryptionKeyStartupWarnings> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var section = configuration.GetSection(Core.ServiceConfiguration.PreviousEncryptionKeysKey);
        if (!string.IsNullOrWhiteSpace(section.Value) && !section.GetChildren().Any())
        {
            logger.LogWarning(
                "{Setting} is a single value, not a list; it is read as one previous key. Write it as a list: {Setting}:0 (the environment variable Beacon__PreviousEncryptionKeys__0)",
                Core.ServiceConfiguration.PreviousEncryptionKeysKey,
                Core.ServiceConfiguration.PreviousEncryptionKeysKey);
        }

        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => WarnAboutValuesUnderPreviousKeysAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal async Task WarnAboutValuesUnderPreviousKeysAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var reEncryption = scope.ServiceProvider.GetService<IStoredValueReEncryptionService>();
            if (reEncryption == null)
            {
                return;
            }

            var count = await reEncryption.CountAsync(cancellationToken);
            if (count.LegacyFormat > 0)
            {
                logger.LogWarning(
                    ProjectAccessEvents.StoredValuesReEncrypted,
                    "{LegacyFormat} stored values are still in the legacy unauthenticated encryption format. Re-encrypt them (IStoredValueReEncryptionService; the sample host runs it as a job at startup), then turn {Setting} off",
                    count.LegacyFormat,
                    Core.ServiceConfiguration.AllowLegacyCbcKey);
            }

            if (!Core.ServiceConfiguration.HasPreviousEncryptionKeys(configuration))
            {
                return;
            }

            if (count.PreviousKeysRemovable)
            {
                logger.LogInformation(
                    ProjectAccessEvents.StoredValuesReEncrypted,
                    "No stored value is encrypted with a key in {Setting}; the previous keys can be removed",
                    Core.ServiceConfiguration.PreviousEncryptionKeysKey);

                return;
            }

            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "{UnderPreviousKeys} stored values are still encrypted with a key in {Setting}, and {Unreadable} with no configured key; {Unresolved} keyed recipient hashes name a definition their recipient does not hold and may still need a previous key. Re-encrypt the values with the current key (IStoredValueReEncryptionService; the sample host runs it as a job at startup) and decide the recipients' pending changes before removing a previous key",
                count.UnderPreviousKeys,
                Core.ServiceConfiguration.PreviousEncryptionKeysKey,
                count.Unreadable,
                count.UnresolvedKeyDependencies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The stored values still encrypted with a previous key or in the legacy format could not be counted ({ExceptionType})",
                ex.GetType().Name);
        }
    }
}
