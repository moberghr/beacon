using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit;

/// <summary>
/// Review finding F4: every other retention test drives <c>RedactTrackedEntriesAsync</c> with a fixture resolver and
/// a <c>Mock&lt;IServiceProvider&gt;</c>, so the path the runtime actually takes — the interceptor resolved from the
/// container, resolving <see cref="IMcpSettingsProvider"/> off the ROOT provider inside a save — was never executed.
/// That path fails closed, so a resolution failure would silently blank content on every MCP write with nothing but
/// warnings to show for it. These tests prove the wiring instead of assuming it.
/// </summary>
[TestFixture]
public class ContentRetentionWiringTests
{
    [Test]
    public void BeaconServices_RegisterThePolicyAndTheInterceptor()
    {
        using var provider = BuildProvider();

        provider.GetService<IContentRetentionPolicy>().Should().BeOfType<ContentRetentionPolicy>();
        provider.GetService<ContentRetentionInterceptor>().Should().NotBeNull();
    }

    [Test]
    public void TheInterceptorResolvesTheSettingsProviderFromTheRootProvider()
    {
        // This is the exact resolution ResolveDecisionAsync performs during SaveChanges. It must work from the
        // ROOT provider (no scope), or the interceptor fails closed and blanks content on every write.
        using var provider = BuildProvider();

        provider.GetRequiredService<IMcpSettingsProvider>().Should().NotBeNull();
    }

    [Test]
    public void ThePostgreSqlContextFactoryCarriesTheInterceptor()
    {
        using var provider = BuildProvider();

        var interceptor = provider.GetRequiredService<ContentRetentionInterceptor>();
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        // Interceptors registered through AddInterceptors live on the options' CoreOptionsExtension, which is
        // what EF reads when it builds the context's interception pipeline.
        var attached = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors ?? [];

        attached.Should().Contain(interceptor,
            "the belt only works if it is attached to the context the factory hands out");
    }

    [Test]
    public void TheSqlServerContextFactoryCarriesTheInterceptor()
    {
        // Review N3: both providers attach the belt, so both are asserted — dropping it from one would otherwise
        // ship green and silently remove the enforcement backstop on that provider.
        using var provider = BuildProvider(useSqlServer: true);

        var interceptor = provider.GetRequiredService<ContentRetentionInterceptor>();
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var attached = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors ?? [];

        attached.Should().Contain(interceptor);
    }

    private static ServiceProvider BuildProvider(bool useSqlServer = false)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        if (useSqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        // ValidateScopes: the interceptor resolves IMcpSettingsProvider off the ROOT provider, so a scoped
        // dependency added to it later must fail here rather than at the first save (review N3). ValidateOnBuild is
        // deliberately NOT set — Core alone is an incomplete container by design (IBeaconUserContext, IRoleService
        // and friends are registered by the host), so eager validation would fail on unrelated handlers.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
