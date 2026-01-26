using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Data;
using Semantico.Core.SqlServer.Data;

namespace Semantico.Core.SqlServer;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures SQL Server as the database provider for Semantico.
    /// This method is chainable after AddSemanticoServices().
    /// </summary>
    public static IServiceCollection UseSqlServer(
        this SemanticoBuilder builder,
        string connectionString,
        string schema = "semantico")
    {
        builder.Services.AddDbContextFactory<SqlServerSemanticoContext>(options =>
            options.UseSqlServer(connectionString,
                    b => b.MigrationsHistoryTable("__EFMigrationsHistory", schema)));

        // Register the base context factory using the SQL Server implementation
        builder.Services.AddSingleton<IDbContextFactory<SemanticoContext>>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<SqlServerSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(factory);
        });

        return builder.Services;
    }

    private class SemanticoContextFactoryAdapter : IDbContextFactory<SemanticoContext>
    {
        private readonly IDbContextFactory<SqlServerSemanticoContext> _factory;

        public SemanticoContextFactoryAdapter(IDbContextFactory<SqlServerSemanticoContext> factory)
        {
            _factory = factory;
        }

        public SemanticoContext CreateDbContext()
        {
            return _factory.CreateDbContext();
        }
    }
}
