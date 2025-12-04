using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Data;
using Semantico.Core.PostgreSql.Data;

namespace Semantico.Core.PostgreSql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlSemantico(
        this IServiceCollection services,
        string connectionString,
        string schema = "semantico")
    {
        services.AddDbContextFactory<PostgreSqlSemanticoContext>(options =>
            options.UseNpgsql(connectionString,
                    builder => builder.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                   .UseSnakeCaseNamingConvention());

        // Register the base context factory using the PostgreSQL implementation
        services.AddSingleton<IDbContextFactory<SemanticoContext>>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<PostgreSqlSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(factory);
        });

        return services;
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
