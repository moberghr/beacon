using System.Security.Claims;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.SignalR;
using Beacon.Core.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The realtime hub addresses a connection by the id Beacon's publishers pass to <c>Clients.User</c>: the session's
/// <c>Users.ExternalId</c> (the actor's <c>UserId</c>). A principal that names its user by another id (an API key's
/// numeric <c>Users.Id</c>, a mapped MCP caller's) gets no user id, so it can never receive another user's events. The
/// provider is registered once, by <c>AddBeaconApiServices</c>, whatever order the host calls <c>AddSignalR</c> in.
/// </summary>
[TestFixture]
public class HubUserIdProviderTests
{
    [Test]
    public void ASignedInSession_IsAddressedByItsNameIdentifier()
    {
        var principal = Principal("cookie", new Claim(ClaimTypes.NameIdentifier, "ext-ana"));

        new HubUserIdProvider().GetUserId(Connection(principal)).Should().Be("ext-ana");
    }

    [Test]
    public void AnApiKey_GetsNoUserId()
    {
        var principal = Principal(
            McpCallerClaimTypes.ApiKeyAuthenticationType,
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod));

        new HubUserIdProvider().GetUserId(Connection(principal)).Should().BeNull("a key names its owner by Users.Id, another id space");
    }

    [Test]
    public void AMappedMcpCaller_GetsNoUserId()
    {
        var principal = Principal(
            "Bearer",
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod));

        new HubUserIdProvider().GetUserId(Connection(principal)).Should().BeNull();
    }

    [Test]
    public void AnAnonymousConnection_GetsNoUserId()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ana")]));

        new HubUserIdProvider().GetUserId(Connection(principal)).Should().BeNull();
    }

    [Test]
    public void AddBeaconApiServices_RegistersBeaconsProvider()
    {
        using var provider = BuildProvider(x => x.AddBeaconApiServices());

        provider.GetRequiredService<IUserIdProvider>().Should().BeOfType<HubUserIdProvider>();
        Registrations(x => x.AddBeaconApiServices()).Should().Be(1);
    }

    [Test]
    public void AddBeaconApiServices_ReplacesSignalRsDefault_WhenTheHostAddedSignalRFirst()
    {
        void Configure(IServiceCollection services)
        {
            services.AddSignalR();
            services.AddBeaconApiServices();
        }

        using var provider = BuildProvider(Configure);

        provider.GetRequiredService<IUserIdProvider>().Should().BeOfType<HubUserIdProvider>();
        Registrations(Configure).Should().Be(1);
    }

    [Test]
    public void ALaterAddSignalR_KeepsBeaconsProvider()
    {
        using var provider = BuildProvider(x =>
        {
            x.AddBeaconApiServices();
            x.AddSignalR();
        });

        provider.GetRequiredService<IUserIdProvider>().Should().BeOfType<HubUserIdProvider>();
    }

    [Test]
    public void AddBeaconApiServices_KeepsAProviderTheHostRegistered()
    {
        using var provider = BuildProvider(x =>
        {
            x.AddSingleton<IUserIdProvider, HostUserIdProvider>();
            x.AddBeaconApiServices();
        });

        provider.GetRequiredService<IUserIdProvider>().Should().BeOfType<HostUserIdProvider>();
    }

    [Test]
    public void AddBeaconHostInfrastructure_RegistersNoSecondProvider()
    {
        void Configure(IServiceCollection services)
        {
            services.AddBeaconApiServices();
            services.AddBeaconHostInfrastructure<NoClaimsTransformation>();
        }

        Registrations(Configure).Should().Be(1);
    }

    [Test]
    public void RealtimeOff_RegistersNoProvider()
    {
        Registrations(x => x.AddBeaconApiServices(y => y.Realtime = false)).Should().Be(0);
    }

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    private static HubConnectionContext Connection(ClaimsPrincipal principal)
    {
        var connection = new DefaultConnectionContext { User = principal };

        return new HubConnectionContext(connection, new HubConnectionContextOptions(), NullLoggerFactory.Instance);
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);

        return services.BuildServiceProvider();
    }

    private static int Registrations(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);

        return services.Count(x => x.ServiceType == typeof(IUserIdProvider));
    }

    private sealed class HostUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection)
        {
            return null;
        }
    }

    private sealed class NoClaimsTransformation : Microsoft.AspNetCore.Authentication.IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            return Task.FromResult(principal);
        }
    }
}
