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
            var options = sp.GetRequiredService<DbContextOptions<PostgreSqlSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(options, schema);
        });

        return services;
    }

    private class SemanticoContextFactoryAdapter : IDbContextFactory<SemanticoContext>
    {
        private readonly DbContextOptions<PostgreSqlSemanticoContext> _options;
        private readonly string _schema;

        public SemanticoContextFactoryAdapter(DbContextOptions<PostgreSqlSemanticoContext> options, string schema)
        {
            _options = options;
            _schema = schema;
        }

        public SemanticoContext CreateDbContext()
        {
            return new PostgreSqlSemanticoContext(_options, _schema);
        }
    }
}
