using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core.HostData;

/// <summary>
/// The host DbContexts this process exposes, keyed by <c>DataSource.HostManagedKey</c>. Each exposure snapshot is
/// built once from the in-memory EF model (no DB round-trip) and cached for the process lifetime — the model
/// cannot change without a restart.
/// </summary>
internal interface IHostDataSourceRegistry
{
    IReadOnlyList<HostDataSourceRegistration> Registrations { get; }

    /// <summary>The snapshot for <paramref name="hostManagedKey"/>, or null when this host does not register it.</summary>
    HostExposureSnapshot? GetSnapshot(string hostManagedKey);
}

internal sealed class HostDataSourceRegistry(
    IEnumerable<HostDataSourceRegistration> registrations,
    IServiceScopeFactory scopeFactory,
    IXmlDocumentationProvider documentation) : IHostDataSourceRegistry
{
    private readonly ConcurrentDictionary<string, Lazy<HostExposureSnapshot>> _snapshots = new(StringComparer.Ordinal);

    public IReadOnlyList<HostDataSourceRegistration> Registrations { get; } = registrations.ToList();

    public HostExposureSnapshot? GetSnapshot(string hostManagedKey)
    {
        var registration = Registrations
            .Where(x => x.Key == hostManagedKey)
            .FirstOrDefault();

        if (registration == null)
        {
            return null;
        }

        // PublicationOnly: a failed build (bad allow-list, unsupported provider) is not cached, so the error
        // surfaces again on the next call instead of a stale exception object.
        var lazy = _snapshots.GetOrAdd(
            hostManagedKey,
            _ => new Lazy<HostExposureSnapshot>(() => Build(registration), LazyThreadSafetyMode.PublicationOnly));

        try
        {
            return lazy.Value;
        }
        catch
        {
            _snapshots.TryRemove(hostManagedKey, out _);
            throw;
        }
    }

    private HostExposureSnapshot Build(HostDataSourceRegistration registration)
    {
        using var scope = scopeFactory.CreateScope();
        var (context, ownedByCaller) = registration.ResolveContext(scope.ServiceProvider);

        try
        {
            var engine = HostModelReader.InferEngine(context.Database.ProviderName, registration.Options.Engine);

            return HostModelReader.Read(ResolveModel(context), engine, registration, documentation);
        }
        finally
        {
            if (ownedByCaller)
            {
                context.Dispose();
            }
        }
    }

    // The design-time model keeps HasComment annotations that the optimized runtime model strips.
    private static IModel ResolveModel(DbContext context)
    {
        try
        {
            return context.GetService<IDesignTimeModel>().Model;
        }
        catch (InvalidOperationException)
        {
            return context.Model;
        }
    }
}
