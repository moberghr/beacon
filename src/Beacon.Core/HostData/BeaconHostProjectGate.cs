using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.HostData;

/// <summary>
/// The host project's id, syncing the host (<c>SyncBeaconHostAsync</c> and the project itself) the first time it is
/// asked for, so a host needs no startup step and no hosted service (§2.15) for it. Until a sync succeeds it answers
/// <c>null</c> — callers that need the project are refused, fail closed — and a failed sync is tried again on a later ask,
/// after a delay that doubles from <see cref="BeaconHostSyncOptions.InitialRetryDelay"/> up to
/// <see cref="BeaconHostSyncOptions.MaxRetryDelay"/>. <see cref="State"/> reports it for a health check.
/// </summary>
public interface IBeaconHostProjectGate
{
    /// <summary>The host project's id, or <c>null</c> while it has not been synced.</summary>
    Task<int?> GetProjectIdAsync(CancellationToken cancellationToken);

    /// <summary>Whether the host has been synced, and how the attempts went.</summary>
    BeaconHostSyncState State { get; }
}

/// <param name="Synced">The host project exists and its documents were synced.</param>
/// <param name="Failures">Failed attempts in a row.</param>
/// <param name="LastError">The type of the last failure's exception; never its message.</param>
/// <param name="NextAttemptAt">When a later ask tries again, while backing off.</param>
public sealed record BeaconHostSyncState(bool Synced, int Failures, string? LastError, DateTimeOffset? NextAttemptAt);

/// <summary>Options of <see cref="IBeaconHostProjectGate"/>.</summary>
public sealed class BeaconHostSyncOptions
{
    /// <summary>The host project. Required.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>The wait after the first failed attempt; doubled after each further one. Default 5 seconds.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts. Default 5 minutes.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row: doubling from the first, capped.</summary>
    public TimeSpan RetryDelay(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);

        // Doubled step by step and stopped at the cap, so no number of failures overflows it.
        var delay = InitialRetryDelay;
        for (var i = 1; i < failures && delay < MaxRetryDelay; i++)
        {
            delay += delay;
        }

        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }
}

/// <summary>The sync itself: the host's declarations, then the host project. A seam so the gate's retries are testable.</summary>
internal interface IBeaconHostSynchronizer
{
    /// <summary>Syncs the host and returns the host project's id.</summary>
    Task<int> SyncAsync(string projectName, CancellationToken cancellationToken);
}

internal sealed class BeaconHostSynchronizer(IServiceProvider services) : IBeaconHostSynchronizer
{
    public async Task<int> SyncAsync(string projectName, CancellationToken cancellationToken)
    {
        await services.SyncBeaconHostAsync(cancellationToken);

        // A host that exposes neither a DbContext nor documents still has its project: the one its tools belong to.
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetService<Authorization.ISystemWorkScope>()?.MarkAsSystemWork();

        return await scope.ServiceProvider
            .GetRequiredService<IHostProjectResolver>()
            .EnsureAsync(projectName, description: null, cancellationToken);
    }
}

internal sealed partial class BeaconHostProjectGate(
    IBeaconHostSynchronizer synchronizer,
    IOptions<BeaconHostSyncOptions> options,
    TimeProvider timeProvider,
    ILogger<BeaconHostProjectGate> logger) : IBeaconHostProjectGate, IDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private int? _projectId;
    private int _failures;
    private string? _lastError;
    private DateTimeOffset? _nextAttemptAt;

    public BeaconHostSyncState State => new(_projectId != null, _failures, _lastError, _nextAttemptAt);

    public async Task<int?> GetProjectIdAsync(CancellationToken cancellationToken)
    {
        if (_projectId is { } synced)
        {
            return synced;
        }

        if (IsBackingOff())
        {
            return null;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_projectId is { } syncedMeanwhile)
            {
                return syncedMeanwhile;
            }

            if (IsBackingOff())
            {
                return null;
            }

            try
            {
                _projectId = await SyncOutsideTheRequestAsync(cancellationToken);
                _failures = 0;
                _lastError = null;
                _nextAttemptAt = null;
                LogSynced(logger, options.Value.ProjectName, _projectId.Value);

                return _projectId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failures++;
                _lastError = ex.GetType().Name;
                var delay = options.Value.RetryDelay(_failures);
                _nextAttemptAt = timeProvider.GetUtcNow() + delay;
                LogFailed(logger, ex, _failures, delay);

                return null;
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public void Dispose() => _sync.Dispose();

    private bool IsBackingOff() => _nextAttemptAt is { } next && timeProvider.GetUtcNow() < next;

    // The sync is Beacon's own work, never the asking request's: run without the request's execution context, so the
    // scope the sync marks as system work does not see the caller of the request (an MCP caller) through the HttpContext.
    private Task<int> SyncOutsideTheRequestAsync(CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => synchronizer.SyncAsync(options.Value.ProjectName, cancellationToken), CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Beacon host project {ProjectName} synced (id {ProjectId}).")]
    private static partial void LogSynced(ILogger logger, string projectName, int projectId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Beacon host sync failed ({Failures} in a row); callers that need the host project are refused until it succeeds. Next attempt after {Delay}.")]
    private static partial void LogFailed(ILogger logger, Exception exception, int failures, TimeSpan delay);
}

/// <summary>Registers <see cref="IBeaconHostProjectGate"/>.</summary>
public static class BeaconHostProjectGateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gate that syncs the host project <see cref="BeaconHostSyncOptions.ProjectName"/> the first time it is
    /// needed (see <see cref="IBeaconHostProjectGate"/>).
    /// </summary>
    public static IServiceCollection AddBeaconHostProjectGate(this IServiceCollection services, Action<BeaconHostSyncOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<BeaconHostSyncOptions>()
            .Configure(configure)
            .Validate(x => !string.IsNullOrWhiteSpace(x.ProjectName), "The host project name is required.");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBeaconHostSynchronizer, BeaconHostSynchronizer>();
        services.TryAddSingleton<IBeaconHostProjectGate, BeaconHostProjectGate>();

        return services;
    }
}
