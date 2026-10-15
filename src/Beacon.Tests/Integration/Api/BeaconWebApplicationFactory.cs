using System.Security.Cryptography;
using Beacon.Api.Authentication;
using Beacon.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// Spins up Beacon.SampleProject's Program in-process for endpoint integration tests.
/// Tests fall back to <see cref="NUnit.Framework.Assert.Inconclusive(string)"/> when the
/// host can't bootstrap (missing DB, Warp backing unreachable, etc.) so unit-test
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

        // The sample commits no encryption key, and Testing reads no user secrets: a key made for this run, unless the
        // environment supplies one.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Beacon__EncryptionKey")))
        {
            builder.UseSetting("Beacon:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        // Testing is not Development: Data Protection needs an application name and a key ring encrypted at rest.
        builder.UseSetting(BeaconDataProtectionExtensions.ApplicationNameKey, "beacon-integration-tests");
        builder.ConfigureTestServices(TestDataProtection.UseEncryptedInMemoryKeyRing);
    }
}
