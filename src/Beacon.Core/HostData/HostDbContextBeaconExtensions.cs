using Beacon.Core.HostData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core;

/// <summary>
/// Host-side API for exposing the host application's own EF Core <c>DbContext</c> as a Beacon data source.
/// </summary>
public static class HostDbContextBeaconExtensions
{
    /// <summary>
    /// Exposes <typeparamref name="TContext"/> as a read-only Beacon data source. Tables are default-deny: allow-list
    /// them in <paramref name="configure"/>. The host must call <see cref="SyncBeaconHostDataSourcesAsync"/> at
    /// startup (after migrations) to create/refresh the data source. Call this before <c>UseSqlServer()</c> /
    /// <c>UsePostgreSql()</c>, which end the builder chain.
    /// </summary>
    public static BeaconBuilder ExposeDbContext<TContext>(this BeaconBuilder builder, Action<HostDbContextOptions> configure)
        where TContext : DbContext
    {
        var options = new HostDbContextOptions();
        configure(options);

        var registration = new HostDataSourceRegistration(typeof(TContext), options);

        var duplicate = builder.Services
            .Select(x => x.ImplementationInstance)
            .OfType<HostDataSourceRegistration>()
            .Any(x => x.Key.Equals(registration.Key, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            throw new InvalidOperationException($"ExposeDbContext: a host data source named '{registration.Name}' is already registered.");
        }

        builder.Services.AddSingleton(registration);

        return builder;
    }

    /// <summary>
    /// Creates or refreshes every data source declared with <c>ExposeDbContext</c> (project, data source, project
    /// link, metadata, FK relationships). Idempotent; a data source whose exposed model hash is unchanged is left
    /// alone. Validates that each read-only connection string is configured and throws if one is missing. Safe to run
    /// from several replicas at once: the sync holds a lock in Beacon's database (<see cref="IHostSyncLock"/>).
    /// </summary>
    public static Task SyncBeaconHostDataSourcesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        return HostSyncRunner.RunLockedAsync(
            services,
            (x, ct) => x.GetRequiredService<HostDataSourceSynchronizer>().SyncAllAsync(ct),
            cancellationToken);
    }
}
