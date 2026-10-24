using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Data;

/// <summary>Serves the provider-specific context factory as the <see cref="BeaconContext"/> factory handlers inject.</summary>
internal sealed class BeaconContextFactoryAdapter<TContext>(IDbContextFactory<TContext> factory) : IDbContextFactory<BeaconContext>
    where TContext : BeaconContext
{
    public BeaconContext CreateDbContext()
    {
        return factory.CreateDbContext();
    }
}
