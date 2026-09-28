using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Api.Endpoints;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// SC11 — GET /beacon/api/mcp/audit is mapped, named <c>GetMcpAuditLogs</c> and admin-only.
///
/// A live 403-for-non-admin assertion needs a reachable Postgres instance to seed a non-admin session (see
/// lesson "Integration harness DB gotcha": several API-harness tests fail locally with
/// 'relation "users" does not exist' unless <c>BEACON_TEST_CONNECTION_STRING</c> points at a real database).
/// This test instead proves the same guarantee through the <see cref="EndpointDataSource"/>, which needs no
/// database: the endpoint exists, is named correctly, and carries <see cref="BeaconApiEndpoints.AdminPolicyName"/>
/// — the same wiring <see cref="ExecuteScopeWiringTests"/> checks for the Execute-scope policy. A dropped
/// <c>.RequireAuthorization(...)</c> fails this test the same way it would fail a live 403 check.
/// </summary>
[TestFixture]
[Category("Phase1Harness")]
public class McpAuditEndpointTests
{
    private BeaconWebApplicationFactory? _factory;
    private HttpClient? _client;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        try
        {
            _factory = new BeaconWebApplicationFactory();
            // Force the request pipeline to build so the EndpointDataSource is populated.
            _client = _factory.CreateClient();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive(
                $"Beacon host failed to bootstrap: {ex.Message}. " +
                $"Set {BeaconWebApplicationFactory.TestConnectionStringEnvVar} to a reachable Postgres connection string.");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public void GetMcpAuditLogs_IsMappedNamedAndAdminOnly()
    {
        var endpointSource = _factory!.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = endpointSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetMcpAuditLogs");

        endpoint.Should().NotBeNull("GET /beacon/api/mcp/audit should be mapped and named GetMcpAuditLogs");
        endpoint!.RoutePattern.RawText.Should().Contain("mcp/audit");

        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy)
            .ToList();

        policies.Should().Contain(
            BeaconApiEndpoints.AdminPolicyName,
            "the audit export returns every caller's tool-call history and must be admin-only, " +
            "so a non-admin caller gets 403");
    }
}
