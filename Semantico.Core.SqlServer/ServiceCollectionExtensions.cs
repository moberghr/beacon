using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Data;
using Semantico.Core.SqlServer.Data;

namespace Semantico.Core.SqlServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerSemantico(
        this IServiceCollection services,
        string connectionString,
        string schema = "semantico")
    {
        services.AddDbContextFactory<SqlServerSemanticoContext>(options =>
            options.UseSqlServer(connectionString,
                    builder => builder.MigrationsHistoryTable("__EFMigrationsHistory", schema)));

        // Register the base context factory using the SQL Server implementation
        services.AddSingleton<IDbContextFactory<SemanticoContext>>(sp =>
        {
            var options = sp.GetRequiredService<DbContextOptions<SqlServerSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(options, schema);
        });

        return services;
    }

    private class SemanticoContextFactoryAdapter : IDbContextFactory<SemanticoContext>
    {
        private readonly DbContextOptions<SqlServerSemanticoContext> _options;
        private readonly string _schema;

        public SemanticoContextFactoryAdapter(DbContextOptions<SqlServerSemanticoContext> options, string schema)
        {
            _options = options;
            _schema = schema;
        }

        public SemanticoContext CreateDbContext()
        {
            return new SqlServerSemanticoContext(_options, _schema);
        }
    }
}
