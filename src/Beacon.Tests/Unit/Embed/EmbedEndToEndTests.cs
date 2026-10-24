using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.SavedQueries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The embed surface over HTTP with the real pieces together: the embed middleware, the endpoints and their filter, MediatR
/// with <see cref="ProjectAuthorizationBehavior{TRequest,TResponse}"/>, the real exchange and render handlers, the caller
/// resolved from the middleware's principal, the real project access evaluator and the saved-query executor over the kit's
/// store. A grant is exchanged, rendered, revoked, and its token is refused from then on.
/// </summary>
[TestFixture]
public class EmbedEndToEndTests
{
    [Test]
    public async Task AGrant_IsExchangedAndRendered_AndRefusedOnceRevoked()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var executed = new List<string>();
        await using var host = await EmbedSurfaceHost.StartAsync(kit, services: x => AddRealPipeline(x, kit, executed));

        var exchange = await host.Client.SendAsync(Exchange(secret, branch: "7"));
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await exchange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var render = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}", kit.Codec.FrameMarkerOf(grant.PublicId));
        render.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await render.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Loan book");
        body.GetProperty("rows").GetArrayLength().Should().Be(1);
        executed.Should().ContainSingle().Which.Should().Contain("@p0");

        grant.RevokedTime = EmbedTestKit.Now.UtcDateTime;
        var refused = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}", kit.Codec.FrameMarkerOf(grant.PublicId));

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await refused.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"embed_unavailable\"}");
        executed.Should().ContainSingle("a revoked grant reaches no SQL");
        host.Audit.Of(SecurityAuditActions.EmbedTokenIssued).Should().ContainSingle();
        host.Audit.Of(SecurityAuditActions.EmbedQueryRendered).Should().ContainSingle().Which.Outcome.Should().Be(SecurityAuditOutcomes.Success);
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused).Should().ContainSingle().Which.DetailsJson.Should().Be("{\"reason\":\"revoked\"}");
    }

    private static HttpRequestMessage Exchange(string secret, string branch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BeaconEmbedPaths.TokensPath)
        {
            Content = JsonContent.Create(new { parameters = new Dictionary<string, string> { ["branch"] = branch } })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"BeaconEmbedGrant {secret}");

        return request;
    }

    private static void AddRealPipeline(IServiceCollection services, EmbedTestKit kit, List<string> executed)
    {
        var factory = kit.Data.Store.Factory().Object;
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataSource _, string sql, Dictionary<string, object?> _, CancellationToken _) =>
            {
                executed.Add(sql);

                return new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["id"] = 1, ["email"] = "ana@example.com" }], TotalRows = 1 };
            });
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(provider.Object);

        services.AddHttpContextAccessor();
        services.AddSingleton(factory);
        services.AddSingleton(Options.Create(new QueryExecutionOptions()));
        services.AddSingleton<EmbedRenderLimiter>();
        services.AddScoped<IBeaconActorAccessor>(x => new PrincipalActorAccessor(x.GetRequiredService<IHttpContextAccessor>(), factory, kit.Clock));
        services.AddScoped(x => TestProjectAccess.RealEvaluator(Current(x), factory));
        services.AddScoped(x =>
            new ProjectRequestAuthorizer(
                x.GetRequiredService<IProjectAccessEvaluator>(),
                new ProjectResourceResolver(),
                factory,
                x.GetRequiredService<IBeaconActorAccessor>(),
                TestPermissions.None,
                NullLogger<ProjectRequestAuthorizer>.Instance));
        services.AddScoped<ISavedQueryRunner>(x =>
            new SavedQueryToolExecutor(
                factory,
                providers.Object,
                TestSqlGate.Create(),
                TestConfiguredDatabases.None,
                new QueryGuardrailService(),
                SettingsProviderMock.Unshared(SettingsProviderMock.Create(new McpSettingsData()).Object),
                x.GetRequiredService<IProjectAccessEvaluator>(),
                TestRowSecurity.Resolver,
                TestQueryCostLimit.Passing,
                Options.Create(new QueryExecutionOptions()),
                NullLoggerFactory.Instance));
        services.AddMediatR(x =>
        {
            x.RegisterServicesFromAssembly(typeof(RenderEmbedQueryCommand).Assembly);
            x.AddOpenBehavior(typeof(ProjectAuthorizationBehavior<,>));
        });
    }

    private static BeaconActor Current(IServiceProvider services)
    {
        return services.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // The caller of the request, read from the principal the embed middleware set, as Beacon's own accessor reads it.
    private sealed class PrincipalActorAccessor(IHttpContextAccessor http, IDbContextFactory<BeaconContext> factory, TimeProvider clock) : IBeaconActorAccessor
    {
        private BeaconActor? _actor;

        public async Task<BeaconActor> GetCurrentAsync(CancellationToken cancellationToken)
        {
            return _actor ??= await BeaconActorAccessor.ResolveAsync(http.HttpContext?.User, factory, cancellationToken, beaconManagesUsers: true, clock);
        }

        public Task<BeaconActor> ResolveOwnerAsync(string? ownerUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(BeaconActor.Nobody);
        }
    }
}
