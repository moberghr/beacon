using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core.HostData;

/// <summary>
/// One <c>ExposeDbContext&lt;TContext&gt;</c> call, captured at registration time. Held as a singleton; the
/// context itself is only resolved (in its own scope) when the model is first read.
/// </summary>
internal sealed class HostDataSourceRegistration
{
    public const string KeyPrefix = "efcore:";

    public HostDataSourceRegistration(Type contextType, HostDbContextOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ReadOnlyConnectionStringName))
        {
            throw new InvalidOperationException(
                $"ExposeDbContext<{contextType.Name}> requires ReadOnlyConnectionStringName (a ConnectionStrings entry for a dedicated read-only login).");
        }

        ContextType = contextType;
        Options = options;
        Name = string.IsNullOrWhiteSpace(options.Name) ? contextType.Name : options.Name.Trim();
        ProjectName = string.IsNullOrWhiteSpace(options.ProjectName) ? Name : options.ProjectName.Trim();
        ConnectionStringName = options.ReadOnlyConnectionStringName.Trim();
        Key = KeyPrefix + Name;
    }

    public Type ContextType { get; }

    public HostDbContextOptions Options { get; }

    public string Name { get; }

    public string ProjectName { get; }

    public string ConnectionStringName { get; }

    /// <summary>Stable identity of the persisted data source (<c>DataSource.HostManagedKey</c>).</summary>
    public string Key { get; }

    /// <summary>
    /// Resolves the host context from a scope: a registered <see cref="IDbContextFactory{TContext}"/> first, the
    /// scoped context otherwise. The returned context is disposed by the caller only when it came from a factory.
    /// </summary>
    public (DbContext Context, bool OwnedByCaller) ResolveContext(IServiceProvider scopedProvider)
    {
        var factoryType = typeof(IDbContextFactory<>).MakeGenericType(ContextType);
        if (scopedProvider.GetService(factoryType) is { } factory)
        {
            var created = factoryType.GetMethod(nameof(IDbContextFactory<DbContext>.CreateDbContext))!.Invoke(factory, null);

            return ((DbContext)created!, true);
        }

        return ((DbContext)scopedProvider.GetRequiredService(ContextType), false);
    }
}
