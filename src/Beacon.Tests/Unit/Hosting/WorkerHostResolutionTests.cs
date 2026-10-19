using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Hosting;

/// <summary>
/// A host that references Beacon.Core without Beacon.Api (a worker service running Beacon's jobs) registers no
/// <see cref="IHttpContextAccessor"/>. Core's scheduled-work services must still resolve there, and every scope is off-request.
/// </summary>
[TestFixture]
public class WorkerHostResolutionTests
{
    [Test]
    public void CoreServicesResolveWithoutAnHttpContextAccessor()
    {
        using var provider = WorkerHost();
        using var scope = provider.CreateScope();

        provider.GetService<IHttpContextAccessor>().Should().BeNull("the worker host registers no HTTP context accessor");
        scope.ServiceProvider.GetRequiredService<IJobService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IBeaconPermissionAccessor>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IRunAsScope>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IBeaconAuthorizationProvider>().Should().NotBeNull();
    }

    [Test]
    public async Task WithoutAnAccessor_AnUnmarkedScope_ActsAsNobody()
    {
        using var provider = WorkerHost();
        using var scope = provider.CreateScope();

        var actor = await scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(CancellationToken.None);

        actor.Should().BeSameAs(BeaconActor.Nobody);
    }

    [Test]
    public async Task WithoutAnAccessor_AMarkedJobScope_ActsAsTheSystem()
    {
        using var provider = WorkerHost();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISystemWorkScope>().MarkAsSystemWork();

        var actor = await scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(CancellationToken.None);

        actor.Kind.Should().Be(BeaconPrincipalKind.System);
    }

    [Test]
    public void WithoutAnAccessor_AScopeCanRunAsTheOwnerOfScheduledWork()
    {
        using var provider = WorkerHost();
        using var scope = provider.CreateScope();
        var runAs = scope.ServiceProvider.GetRequiredService<IRunAsScope>();

        runAs.RunAs("ext-owner");

        runAs.IsSet.Should().BeTrue();
        runAs.OwnerUserId.Should().Be("ext-owner");
    }

    [Test]
    public async Task UserManagementOn_WithoutAnAccessor_TheAuthorizationProviderFindsNoCaller()
    {
        var users = new Mock<IUserManagementService>(MockBehavior.Strict);
        using var provider = WorkerHost(users.Object);
        using var scope = provider.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IBeaconAuthorizationProvider>();

        var canRead = await authorization.HasReadPermissionAsync();

        authorization.Should().BeOfType<DatabaseAuthorizationProvider>();
        canRead.Should().BeFalse("off-request there is no caller to authorize");
        users.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UserManagementOn_WithAnAccessor_TheAuthorizationProviderReadsTheRequestsCaller()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetUserByExternalIdAsync("ext-ada", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData
            {
                Id = 3,
                ExternalId = "ext-ada",
                UserName = "ada",
                IsEnabled = true,
                Roles = [new BeaconRoleData { Name = "Viewer", Level = 1 }]
            });
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Cookies"))
            }
        };
        using var provider = WorkerHost(users.Object, http);
        using var scope = provider.CreateScope();

        var canRead = await scope.ServiceProvider.GetRequiredService<IBeaconAuthorizationProvider>().HasReadPermissionAsync();

        canRead.Should().BeTrue("with an accessor registered DI picks the constructor that reads the request");
        users.Verify(x => x.GetUserByExternalIdAsync("ext-ada", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ServiceProvider WorkerHost(IUserManagementService? users = null, IHttpContextAccessor? http = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (http != null)
        {
            services.AddSingleton(http);
        }

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            if (users != null)
            {
                x.EnableUserManagement();
            }
        });
        builder.AddSqlServerConnector();
        builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");

        // Core registers no IBeaconUserContext (Beacon.Api does): a host without Api supplies its own.
        services.AddScoped(_ => Mock.Of<IBeaconUserContext>());

        if (users != null)
        {
            services.AddSingleton(users);
        }

        // ValidateOnBuild is not set: Core alone is an incomplete container by design (Api-only services are absent).
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
