using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Data;
using Semantico.Core.PostgreSql.Data;

namespace Semantico.Core.PostgreSql;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures PostgreSQL as the database provider for Semantico.
    /// This method is chainable after AddSemanticoServices().
    /// </summary>
    public static IServiceCollection UsePostgreSql(
        this SemanticoBuilder builder,
        string connectionString,
        string schema = "semantico")
    {
        builder.Services.AddDbContextFactory<PostgreSqlSemanticoContext>(options =>
            options.UseNpgsql(connectionString,
                    b => b.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                   .UseSnakeCaseNamingConvention());

        // Register the base context factory using the PostgreSQL implementation
        builder.Services.AddSingleton<IDbContextFactory<SemanticoContext>>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<PostgreSqlSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(factory);
        });

        // Register SemanticoContext as scoped (required for Data Protection key persistence)
        builder.Services.AddScoped<SemanticoContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<SemanticoContext>>().CreateDbContext());

        return builder.Services;
    }

    private class SemanticoContextFactoryAdapter : IDbContextFactory<SemanticoContext>
    {
        private readonly IDbContextFactory<PostgreSqlSemanticoContext> _factory;

        public SemanticoContextFactoryAdapter(IDbContextFactory<PostgreSqlSemanticoContext> factory)
        {
            _factory = factory;
        }

        public SemanticoContext CreateDbContext()
        {
            return _factory.CreateDbContext();
        }
    }
}
