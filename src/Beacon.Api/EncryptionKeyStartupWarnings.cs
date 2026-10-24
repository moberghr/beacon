using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Warns at startup about the keys stored values may still be encrypted with (<c>Beacon:PreviousEncryptionKeys</c>): when
/// the setting is a single value rather than a list (it is read as one key), and, after the host has started, off the
/// startup thread and best effort, how many stored values are still under a previous key, how many no configured key
/// decrypts and how many keyed hashes (of recipients and data sources) and pending LLM connection changes cannot be
/// rewritten under the current key
/// (<see cref="IStoredValueReEncryptionService.CountAsync"/>), so an operator knows whether the previous keys can be
/// removed. Counts and configuration names only, never a key (§1.11); a database that cannot be read is noted, never a
/// failure.
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

        if (!Core.ServiceConfiguration.HasPreviousEncryptionKeys(configuration))
        {
            return next;
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
                "{UnderPreviousKeys} stored values are still encrypted with a key in {Setting}, and {Unreadable} with no configured key; {Unresolved} keyed hashes name a definition their recipient or data source does not hold, or belong to a pending LLM connection change, and may still need a previous key. Re-encrypt the values with the current key (IStoredValueReEncryptionService; the sample host runs it as a job at startup) and decide the pending changes before removing a previous key",
                count.UnderPreviousKeys,
                Core.ServiceConfiguration.PreviousEncryptionKeysKey,
                count.Unreadable,
                count.UnresolvedKeyDependencies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The stored values still encrypted with a previous key could not be counted ({ExceptionType})",
                ex.GetType().Name);
        }
    }
}
