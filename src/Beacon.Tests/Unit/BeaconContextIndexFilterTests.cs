using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The two filtered unique indexes whose filter text depends on the provider (SQL Server keeps PascalCase column names in
/// brackets, PostgreSQL uses snake_case): the model tells the providers apart by the provider name, gives each one its own
/// filter, and matches each provider's latest migration snapshot, so nothing about the stored schema changes.
/// </summary>
[TestFixture]
public class BeaconContextIndexFilterTests
{
    private const string PostgreSqlToolNameFilter = "mcp_tool_name IS NOT NULL AND archived_time IS NULL";
    private const string PostgreSqlUserNameFilter = "archived_time IS NULL";
    private const string SqlServerToolNameFilter = "[McpToolName] IS NOT NULL AND [ArchivedTime] IS NULL";
    private const string SqlServerUserNameFilter = "[ArchivedTime] IS NULL";

    [Test]
    public void TheTranslationContexts_GetTheirProvidersFilters()
    {
        using var postgreSql = NpgsqlTestContext.Create();
        using var sqlServer = SqlServerTestContext.Create();

        Filters(postgreSql).Should().Be((PostgreSqlToolNameFilter, PostgreSqlUserNameFilter));
        Filters(sqlServer).Should().Be((SqlServerToolNameFilter, SqlServerUserNameFilter));
    }

    [TestCase(false, PostgreSqlToolNameFilter, PostgreSqlUserNameFilter)]
    [TestCase(true, SqlServerToolNameFilter, SqlServerUserNameFilter)]
    public void TheProviderContext_GetsItsFilters_AndHasNoPendingModelChanges(bool sqlServer, string toolNameFilter, string userNameFilter)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        Filters(context).Should().Be((toolNameFilter, userNameFilter));
        context.Database.HasPendingModelChanges().Should().BeFalse("the latest migration snapshot holds the same filters");
    }

    private static (string? ToolName, string? UserName) Filters(BeaconContext context)
    {
        var toolName = context.Model.FindEntityType(typeof(Query))!
            .GetIndexes()
            .Single(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(Query.McpToolName))
            .GetFilter();
        var userName = context.Model.FindEntityType(typeof(BeaconUser))!
            .GetIndexes()
            .Single(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(BeaconUser.UserName))
            .GetFilter();

        return (toolName, userName);
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (sqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
