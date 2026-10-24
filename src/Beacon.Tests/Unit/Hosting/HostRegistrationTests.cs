using Beacon.AI;
using Beacon.AI.Services.GitHub;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;
using Beacon.Core.PostgreSql;
using Beacon.Core.Worker;
using Beacon.Gateway;
using Beacon.MCP;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Hosting;

/// <summary>
/// The shared host services (request accessor, memory cache, code analyzer, SignalR user id) are registered once per
/// composition however many entry points a host calls, and a registration the host made first stays.
/// </summary>
[TestFixture]
public class HostRegistrationTests
{
    [Test]
    public void ApiComposition_RegistersTheSharedServicesOnce()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconApiServices();

        Count<IHttpContextAccessor>(services).Should().Be(1);
        Count<IMemoryCache>(services).Should().Be(1);
        Count<IUserIdProvider>(services).Should().Be(1);
    }

    [Test]
    public void McpComposition_RegistersTheSharedServicesOnce()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconAI(Configuration());
        services.AddBeaconMcp();

        Count<IHttpContextAccessor>(services).Should().Be(1);
        Count<IMemoryCache>(services).Should().Be(1);
        Count<ICodeAnalyzer>(services).Should().Be(1);
    }

    [Test]
    public void GatewayComposition_RegistersTheSharedServicesOnce()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconMcpGateway(configuration, x =>
        {
            x.ProjectName = "host";
            x.Database(db => db.UsePostgreSql(Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused")));
            x.Entra(configuration.GetSection("AzureAd"));
            x.Users<GatewayUsers>();
        });

        Count<IHttpContextAccessor>(services).Should().Be(1);
        Count<IMemoryCache>(services).Should().Be(1);
        Count<ICodeAnalyzer>(services).Should().Be(1);
        Count<IUserIdProvider>(services).Should().Be(0, "a gateway has no realtime hub");
    }

    [Test]
    public void AllEntryPointsTogether_StillRegisterEachOnce()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconAI(Configuration());
        services.AddBeaconMcp();
        services.AddBeaconApiServices();

        Count<IHttpContextAccessor>(services).Should().Be(1);
        Count<IMemoryCache>(services).Should().Be(1);
        Count<ICodeAnalyzer>(services).Should().Be(1);
        Count<IUserIdProvider>(services).Should().Be(1);
    }

    [Test]
    public void AHostsOwnCodeAnalyzer_RegisteredFirst_Wins()
    {
        var services = Core(new ServiceCollection());
        services.AddTransient<ICodeAnalyzer, HostAnalyzer>();
        services.AddBeaconAI(Configuration());

        Count<ICodeAnalyzer>(services).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICodeAnalyzer>().Should().BeOfType<HostAnalyzer>();
    }

    [Test]
    public void AHostsOwnUserIdProvider_RegisteredFirst_Wins()
    {
        var services = Core(new ServiceCollection());
        services.AddSingleton<IUserIdProvider, HostUserIdProvider>();
        services.AddBeaconApiServices();

        Count<IUserIdProvider>(services).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IUserIdProvider>().Should().BeOfType<HostUserIdProvider>();
    }

    private static int Count<T>(IServiceCollection services) =>
        services
            .Where(x => x.ServiceType == typeof(T))
            .Count();

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
                ["Beacon:PublicBaseUrl"] = "https://host.example.com",
                ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222"
            })
            .Build();

    private static IServiceCollection Core(IServiceCollection services)
    {
        services.AddLogging();
        services.AddBeaconServices(Configuration(), x => x.AddBeaconScheduler<NoOpScheduler>());

        return services;
    }

    private sealed class HostAnalyzer : ICodeAnalyzer
    {
        public string Language => "host";

        public bool CanAnalyze(string filePath) => false;

        public List<CodeReference> AnalyzeFile(string filePath, string content) => [];
    }

    private sealed class GatewayUsers : IMcpHostUserResolver
    {
        public Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken) =>
            Task.FromResult<McpHostUser?>(null);
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    private sealed class HostUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) => null;
    }
}
