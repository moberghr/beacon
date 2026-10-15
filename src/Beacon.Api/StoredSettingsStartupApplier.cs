using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Applies the settings an Admin stored (base URL, LLM provider configuration) when the host starts, so they survive a
/// restart instead of applying only when saved. Runs after the host has started, off the startup thread, best effort
/// (<see cref="StoredSettingsApplier"/>): until it has run, and when the database cannot be read, the host's configuration
/// stays in force.
/// </summary>
internal sealed class StoredSettingsStartupApplier : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => ApplyAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var applier = scope.ServiceProvider.GetService<StoredSettingsApplier>();
        if (applier == null)
        {
            return;
        }

        await applier.ApplyAsync(cancellationToken);
    }
}
