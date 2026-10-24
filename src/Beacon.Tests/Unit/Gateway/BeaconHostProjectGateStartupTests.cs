using Beacon.Core.HostData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// The host project gate's options are validated when the host starts: a gate without a project name fails the host at
/// boot instead of refusing the first caller that needs the host project.
/// </summary>
[TestFixture]
public class BeaconHostProjectGateStartupTests
{
    [Test]
    public async Task WithoutAProjectName_TheHostFailsAtStart()
    {
        using var host = Build(projectName: " ");

        var start = () => host.StartAsync();

        (await start.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("The host project name is required.");
    }

    [Test]
    public async Task WithAProjectName_TheHostStarts()
    {
        using var host = Build(projectName: "inventhor");

        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost Build(string projectName)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Services.AddBeaconHostProjectGate(x => x.ProjectName = projectName);

        return builder.Build();
    }
}
