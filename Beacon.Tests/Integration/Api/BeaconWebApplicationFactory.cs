using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// Spins up Beacon.SampleProject's Program in-process for endpoint integration tests.
/// Tests fall back to <see cref="NUnit.Framework.Assert.Inconclusive(string)"/> when the
/// host can't bootstrap (missing DB, Hangfire backing unreachable, etc.) so unit-test
/// machines without a Postgres instance still get a green build.
/// </summary>
public sealed class BeaconWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionStringEnvVar = "BEACON_TEST_CONNECTION_STRING";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting (not ConfigureAppConfiguration) — with minimal hosting the app's own
        // appsettings.json loads after factory-added configuration sources and would win;
        // UseSetting values are applied on top of the app's configuration.
        var testConnectionString = Environment.GetEnvironmentVariable(TestConnectionStringEnvVar);
        if (!string.IsNullOrWhiteSpace(testConnectionString))
        {
            builder.UseSetting("ConnectionStrings:BeaconContext", testConnectionString);
        }
    }
}
