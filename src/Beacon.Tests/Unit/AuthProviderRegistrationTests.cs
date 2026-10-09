using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 — which auth providers <see cref="ServiceConfiguration.AddBeaconServices"/> registers. Every registration is a
/// TryAdd, so the first one wins: an explicitly configured provider type beats everything, user management then
/// brings the database-backed providers, and the allow-all / always-fail <c>Default*</c> providers are only the last
/// fallback. Before the fix the <c>Default*</c> providers were registered first and silently shadowed the database
/// ones, so a host with user management on (and no explicit provider) enforced nothing.
/// </summary>
[TestFixture]
public class AuthProviderRegistrationTests
{
    [Test]
    public void UserManagementOn_NoProviderTypes_RegistersTheDatabaseProviders()
    {
        var services = Register(x => x.EnableUserManagement());

        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DatabaseAuthorizationProvider>();
        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<DatabaseAuthenticationProvider>();
    }

    [Test]
    public void UserManagementOn_WithJwtExternalLogin_RegistersTheHybridAuthenticationProvider()
    {
        var services = Register(x =>
        {
            x.EnableUserManagement();
            x.Authentication.Jwt = new JwtAuthenticationOptions
            {
                ExternalLoginEndpoint = "https://auth.example.test/api/login",
                Validation = new JwtValidationOptions
                {
                    SigningKey = "test-signing-key-not-a-secret-0123456789",
                    ValidIssuer = "https://auth.example.test",
                    ValidAudience = "beacon"
                }
            };
        });

        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<HybridAuthenticationProvider>();
        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DatabaseAuthorizationProvider>();
    }

    [Test]
    public void UserManagementOn_ExplicitProviderTypes_StillWin()
    {
        var services = Register(x =>
        {
            x.EnableUserManagement();
            x.AddAuthorizationProvider<DefaultAuthorizationProvider>();
            x.AddAuthenticationProvider<DefaultAuthenticationProvider>();
        });

        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DefaultAuthorizationProvider>();
        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<DefaultAuthenticationProvider>();
    }

    [Test]
    public void UserManagementOn_ExplicitAuthenticationOnly_KeepsDatabaseAuthorization()
    {
        // The sample host's shape: an explicit authentication provider and no authorization provider.
        var services = Register(x =>
        {
            x.EnableUserManagement();
            x.Authorization.Enabled = true;
            x.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        });

        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DatabaseAuthorizationProvider>();
        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<DatabaseAuthenticationProvider>();
    }

    [Test]
    public void UserManagementOff_NoProviderTypes_RegistersTheDefaultProviders()
    {
        var services = Register(_ => { });

        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DefaultAuthorizationProvider>();
        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<DefaultAuthenticationProvider>();
    }

    [Test]
    public void UserManagementOff_ExplicitProviderTypes_Win()
    {
        var services = Register(x =>
        {
            x.AddAuthorizationProvider<DatabaseAuthorizationProvider>();
            x.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        });

        ImplementationOf<IBeaconAuthorizationProvider>(services).Should().Be<DatabaseAuthorizationProvider>();
        ImplementationOf<IBeaconAuthenticationProvider>(services).Should().Be<DatabaseAuthenticationProvider>();
    }

    private static ServiceCollection Register(Action<BeaconConfiguration> configure)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoOpScheduler>();
            configure(x);
        });

        return services;
    }

    // TryAdd keeps the first registration only, so exactly one descriptor may exist — two would mean a plain Add
    // slipped in and the last one (not the intended one) would resolve.
    private static Type? ImplementationOf<TService>(IServiceCollection services) =>
        services
            .Where(x => x.ServiceType == typeof(TService))
            .Should()
            .ContainSingle()
            .Subject
            .ImplementationType;

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
