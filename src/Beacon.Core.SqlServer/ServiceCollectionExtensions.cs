using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Core.Data;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.SqlServer.Data;

namespace Beacon.Core.SqlServer;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures SQL Server as the database provider for Beacon, reading the connection string from
    /// <c>ConnectionStrings:BeaconContext</c>. Beacon's schema is always <c>beacon</c>; <c>Beacon:Schema</c> set to any
    /// other value is refused.
    /// </summary>
    public static IServiceCollection UseSqlServer(this BeaconBuilder builder)
    {
        var connectionString = BeaconDatabaseConfiguration.GetConnectionString(builder.Configuration);
        BeaconDatabaseConfiguration.GetSchema(builder.Configuration);

        return builder.UseSqlServer(connectionString);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use UseSqlServer(connectionString). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection UseSqlServer(this BeaconBuilder builder, string connectionString, string schema)
    {
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        return builder.UseSqlServer(connectionString);
    }

    /// <summary>
    /// Configures SQL Server as the database provider for Beacon, in the <c>beacon</c> schema.
    /// This method is chainable after AddBeaconServices().
    /// </summary>
    public static IServiceCollection UseSqlServer(this BeaconBuilder builder, string connectionString)
    {
        builder.Services.AddPooledDbContextFactory<SqlServerBeaconContext>((sp, options) =>
            options.UseBeaconSqlServerModel(connectionString)
                .AddInterceptors(sp.GetRequiredService<ContentRetentionInterceptor>(), sp.GetRequiredService<SecurityAuditInterceptor>()));

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

    /// <summary>
    /// The model and migration options Beacon's SQL Server context uses, shared by <c>UseSqlServer</c> and
    /// <see cref="BeaconSqlServerMigrator"/>: the <c>beacon</c> schema's history table and the migration SQL generator
    /// that places the committed migrations' operations in <c>beacon</c>.
    /// </summary>
    internal static DbContextOptionsBuilder UseBeaconSqlServerModel(this DbContextOptionsBuilder options, string connectionString)
    {
        return options
            .UseSqlServer(connectionString, x => x.MigrationsHistoryTable("__EFMigrationsHistory", BeaconDatabaseConfiguration.DefaultSchema))
            .ReplaceService<IMigrationsSqlGenerator, SchemaAwareMigrationsSqlGenerator>();
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
