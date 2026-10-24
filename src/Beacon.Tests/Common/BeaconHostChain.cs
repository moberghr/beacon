using System.Security.Claims;
using Beacon.AI;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Worker;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Beacon.Tests.Common;

/// <summary>
/// A host registered through the fluent chain: <c>AddBeacon</c>, <c>AddAI</c>, <c>AddMcp</c>, <c>AddApi</c>,
/// <c>AddCookieAuth</c> and <c>UsePostgreSql</c> on a data source that is never opened (§4.7: no database). The
/// configuration it is given must carry <c>Beacon:EncryptionKey</c> (<see cref="TestEncryptionKey"/>).
/// </summary>
internal static class BeaconHostChain
{
    public static BeaconBuilder Add(IServiceCollection services, IConfiguration configuration, Action<BeaconConfiguration>? configure = null)
    {
        var builder = services
            .AddBeacon(configuration, x =>
            {
                x.AddBeaconScheduler<NoOpScheduler>();
                configure?.Invoke(x);
            })
            .AddAI()
            .AddMcp()
            .AddApi()
            .AddCookieAuth<PassThroughClaims>();
        builder.UsePostgreSql(NpgsqlDataSource.Create("Host=localhost;Database=unused"));

        return builder;
    }

    /// <summary>The configuration values a chain host needs to start.</summary>
    public static Dictionary<string, string?> RequiredSettings()
    {
        return new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
            ["Beacon:PublicBaseUrl"] = "https://beacon.example.test"
        };
    }

    private sealed class PassThroughClaims : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) => Task.FromResult(principal);
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
