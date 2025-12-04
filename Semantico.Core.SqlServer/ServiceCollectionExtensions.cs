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
            var factory = sp.GetRequiredService<IDbContextFactory<SqlServerSemanticoContext>>();
            return new SemanticoContextFactoryAdapter(factory);
        });

        return services;
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
