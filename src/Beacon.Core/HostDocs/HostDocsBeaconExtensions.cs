using Beacon.Core.HostData;
using Beacon.Core.HostDocs;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core;

/// <summary>
/// Host-side API for shipping documentation with the host and importing it into a Beacon project, plus the single
/// startup entrypoint that syncs everything a host declares (<c>ExposeDbContext</c> and <c>ExposeDocs</c>).
/// </summary>
public static class HostDocsBeaconExtensions
{
    /// <summary>
    /// Imports markdown / text documents the host ships (files under the content root or embedded resources) into
    /// the project named by <see cref="HostDocsOptions.ProjectName"/>. May be called more than once. The host must
    /// call <see cref="SyncBeaconHostAsync"/> at startup (after migrations). Call this before <c>UseSqlServer()</c> /
    /// <c>UsePostgreSql()</c>, which end the builder chain.
    /// </summary>
    public static BeaconBuilder ExposeDocs(this BeaconBuilder builder, Action<HostDocsOptions> configure)
    {
        var options = new HostDocsOptions();
        configure(options);

        var registration = new HostDocsRegistration(options);

        var existingKeys = builder.Services
            .Select(x => x.ImplementationInstance)
            .OfType<HostDocsRegistration>()
            .Where(x => x.ProjectName == registration.ProjectName)
            .SelectMany(x => x.Sources)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        var duplicate = registration.Sources
            .Where(x => existingKeys.Contains(x.Key))
            .Select(x => x.Key)
            .FirstOrDefault();

        if (duplicate != null)
        {
            throw new InvalidOperationException($"ExposeDocs: source '{duplicate}' is already registered for project '{registration.ProjectName}'.");
        }

        builder.Services.AddSingleton(registration);

        return builder;
    }

    /// <summary>
    /// Syncs everything the host declares: first every <c>ExposeDbContext</c> data source (as
    /// <see cref="HostDbContextBeaconExtensions.SyncBeaconHostDataSourcesAsync"/> does), then every <c>ExposeDocs</c>
    /// document set. Idempotent; unchanged data sources and documents are left alone. Call once at startup, after
    /// migrations.
    /// </summary>
    public static async Task SyncBeaconHostAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.SyncBeaconHostDataSourcesAsync(cancellationToken);

        using var scope = services.CreateScope();
        var synchronizer = scope.ServiceProvider.GetRequiredService<HostDocsSynchronizer>();

        await synchronizer.SyncAllAsync(cancellationToken);
    }
}
