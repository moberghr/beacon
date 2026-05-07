using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Core.Data;
using Beacon.Core.SqlServer.Data;

namespace Beacon.Core.SqlServer;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures SQL Server as the database provider for Beacon.
    /// This method is chainable after AddBeaconServices().
    /// </summary>
    public static IServiceCollection UseSqlServer(
        this BeaconBuilder builder,
        string connectionString,
        string schema = "beacon")
    {
        builder.Services.AddDbContextFactory<SqlServerBeaconContext>(options =>
            options.UseSqlServer(connectionString,
                    b => b.MigrationsHistoryTable("__EFMigrationsHistory", schema)));

        // Register the base context factory using the SQL Server implementation
        builder.Services.AddSingleton<IDbContextFactory<BeaconContext>>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<SqlServerBeaconContext>>();
            return new BeaconContextFactoryAdapter(factory);
        });

        // Register BeaconContext as scoped (required for Data Protection key persistence)
        builder.Services.AddScoped<BeaconContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext());

        return builder.Services;
    }

    private class BeaconContextFactoryAdapter : IDbContextFactory<BeaconContext>
    {
        private readonly IDbContextFactory<SqlServerBeaconContext> _factory;

        public BeaconContextFactoryAdapter(IDbContextFactory<SqlServerBeaconContext> factory)
        {
            _factory = factory;
        }

        public BeaconContext CreateDbContext()
        {
            return _factory.CreateDbContext();
        }
    }
}
