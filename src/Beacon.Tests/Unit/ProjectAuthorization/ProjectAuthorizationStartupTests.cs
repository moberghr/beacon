using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A host that restricts every project (<c>Beacon:Authorization:RequireProjectMembership</c>) without any project
/// membership to grant access fails when it starts, through the real registrations and options validation: no request is
/// ever served by a deployment where only Admins could reach a project. With Beacon user management, the same host starts.
/// </summary>
[TestFixture]
public class ProjectAuthorizationStartupTests
{
    [Test]
    public async Task RestrictingEveryProject_WithoutMemberships_FailsTheHostAtStart()
    {
        using var host = Build(requireMembership: true, userManagement: false);

        var start = () => host.StartAsync();

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("Beacon:Authorization:RequireProjectMembership");
    }

    [Test]
    public async Task RestrictingEveryProject_WithBeaconUserManagement_Starts()
    {
        using var host = Build(requireMembership: true, userManagement: true);

        await host.StartAsync();
        await host.StopAsync();
    }

    [Test]
    public async Task LeavingProjectsOpen_WithoutMemberships_Starts()
    {
        using var host = Build(requireMembership: false, userManagement: false);

        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost Build(bool requireMembership, bool userManagement)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
            ["Beacon:Authorization:RequireProjectMembership"] = requireMembership ? "true" : "false"
        });
        builder.Services.AddBeaconServices(builder.Configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            if (userManagement)
            {
                x.EnableUserManagement();
            }
        });

        // Never opened: validation only asks whether memberships are available.
        builder.Services.AddSingleton<IDbContextFactory<BeaconContext>>(new SqlCapture().Factory());

        return builder.Build();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
