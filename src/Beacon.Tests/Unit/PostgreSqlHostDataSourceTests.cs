using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql;
using Beacon.Core.PostgreSql.Data;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

// A host on passwordless (Entra token) auth owns an NpgsqlDataSource whose connection string has no password, so
// Beacon must open every connection from that data source rather than rebuilding one from a connection string.
[TestFixture]
public class PostgreSqlHostDataSourceTests
{
    private const string HostConnectionString = "Host=localhost;Database=hostdb;Username=host;Application Name=host-app";

    [Test]
    public void UsePostgreSql_DataSource_ContextOpensConnectionsFromTheSuppliedDataSource()
    {
        using var dataSource = NpgsqlDataSource.Create(HostConnectionString);
        using var provider = BuildProvider(x => x.UsePostgreSql(dataSource));
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        NpgsqlDataSourceOf(context).Should().BeSameAs(dataSource);
        AssertRawConnectionComesFromHost(context);
    }

    [Test]
    public void UsePostgreSql_DataSourceFactory_ResolvesTheHostRegisteredSingleton()
    {
        using var dataSource = NpgsqlDataSource.Create(HostConnectionString);
        using var provider = BuildProvider(
            x => x.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>()),
            services => services.AddSingleton(dataSource));
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        NpgsqlDataSourceOf(context).Should().BeSameAs(dataSource);
        AssertRawConnectionComesFromHost(context);
    }

    // The scoped BeaconContext (Data Protection key persistence) goes through the same factory.
    [Test]
    public void UsePostgreSql_DataSource_ScopedContextUsesTheSuppliedDataSource()
    {
        using var dataSource = NpgsqlDataSource.Create(HostConnectionString);
        using var provider = BuildProvider(x => x.UsePostgreSql(dataSource));
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<BeaconContext>();

        NpgsqlDataSourceOf(context).Should().BeSameAs(dataSource);
    }

    // The host's search path is not Beacon's, so Beacon sets its own on each connection it opens.
    [Test]
    public void UsePostgreSql_DataSource_SetsBeaconSearchPathOnOpen()
    {
        using var dataSource = NpgsqlDataSource.Create(HostConnectionString);
        using var provider = BuildProvider(x => x.UsePostgreSql(dataSource));
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        InterceptorsOf(context).Should().ContainSingle(x => x is BeaconSearchPathInterceptor);
    }

    // The connection-string overload is unchanged: the search path lives in the data source it builds, no interceptor.
    [Test]
    public void UsePostgreSql_ConnectionString_KeepsSearchPathInItsOwnDataSource()
    {
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused"));
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        InterceptorsOf(context).Should().NotContain(x => x is BeaconSearchPathInterceptor);
        new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).SearchPath.Should().Be("beacon,public");
    }

    [Test]
    public void UsePostgreSql_DataSource_RejectsSchemaOtherThanBeacon()
    {
        using var dataSource = NpgsqlDataSource.Create(HostConnectionString);

        var act = () => BuildProvider(x => x.UsePostgreSql(dataSource, "tenant_a"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*only supports*beacon*tenant_a*");
    }

    [Test]
    public void BeaconSearchPathInterceptor_RejectsMalformedSchema()
    {
        var act = () => new BeaconSearchPathInterceptor("beacon; DROP TABLE x");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid schema identifier*");
    }

    // HostSyncLock and every other raw-SQL path take their connection from the context, never a connection string.
    private static void AssertRawConnectionComesFromHost(BeaconContext context)
    {
        var connection = context.Database.GetDbConnection();

        connection.Should().BeOfType<NpgsqlConnection>();
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        builder.ApplicationName.Should().Be("host-app");
        builder.Database.Should().Be("hostdb");
        builder.SearchPath.Should().BeNull();
    }

#pragma warning disable EF1001 // NpgsqlOptionsExtension is the only place the configured data source is observable.
    private static NpgsqlDataSource? NpgsqlDataSourceOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<NpgsqlOptionsExtension>()!.DataSource as NpgsqlDataSource;
#pragma warning restore EF1001

    private static IEnumerable<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> InterceptorsOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors ?? [];

    private static ServiceProvider BuildProvider(Action<BeaconBuilder> usePostgreSql, Action<IServiceCollection>? configureHost = null)
    {
        var services = new ServiceCollection();
        configureHost?.Invoke(services);

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        usePostgreSql(builder);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
