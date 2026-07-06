using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Api.Endpoints;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// §1.4 — the Execute-scope policy is only effective if it is actually attached to the
/// SQL-executing endpoints. <see cref="BeaconApiScopePolicyTests"/> proves the policy logic;
/// this proves the wiring, so a future refactor that drops <c>.RequireAuthorization(...)</c>
/// fails the build instead of silently re-opening the bypass.
/// </summary>
[TestFixture]
[Category("Phase1Harness")]
public class ExecuteScopeWiringTests
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

    [TestCase("ExecuteQueryPreview")]
    [TestCase("ExecuteStepPreview")]
    public void SqlExecutionEndpoint_CarriesExecuteScopePolicy(string endpointName)
    {
        var endpointSource = _factory!.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = endpointSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName);

        endpoint.Should().NotBeNull($"endpoint '{endpointName}' should be mapped");

        var policies = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy)
            .ToList();

        policies.Should().Contain(
            BeaconApiEndpoints.ExecuteScopePolicyName,
            $"the SQL-executing endpoint '{endpointName}' must require the Execute scope (§1.4)");
    }
}
