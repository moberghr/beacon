using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using NUnit.Framework;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// Tripwire that fails CI if a MediatR handler is added without a matching HTTP endpoint
/// under <c>/beacon/api/*</c>. Phase 3 page migration will introduce new handlers; the
/// owner of those changes is responsible for wrapping each one (see ProjectsEndpoints.cs
/// for the pattern).
/// </summary>
[TestFixture]
[Category("Phase1Contract")]
public class OpenApiContractTests
{
    private BeaconWebApplicationFactory? _factory;
    private HttpClient? _client;

    /// <summary>
    /// Request types intentionally not exposed via REST. Add only with a code-review note
    /// explaining why — every absence from the API surface is a deliberate decision.
    /// </summary>
    private static readonly HashSet<string> AllowedExclusions = new(StringComparer.Ordinal)
    {
        // Internal-only: triggered by Warp recurring jobs, not user-driven.
    };

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        try
        {
            _factory = new BeaconWebApplicationFactory();
            _client = _factory.CreateClient();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Beacon host failed to bootstrap: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task EveryMediatRHandlerIsExposedViaHttp()
    {
        // 1. Find every IRequest<TResult> / IRequest in Beacon.Core and Beacon.AI handler assemblies.
        // The Core request type is public (handlers are internal). Beacon.AI is grabbed via
        // its assembly attribute through an exposed AI service interface.
        var handlerAssemblies = new[]
        {
            typeof(Beacon.Core.Handlers.Projects.GetProjectsQuery).Assembly,
            typeof(Beacon.AI.ServiceConfiguration).Assembly,
        };

        var requestTypes = handlerAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(IsMediatRRequest)
            .Where(t => !AllowedExclusions.Contains(t.FullName ?? t.Name))
            .ToList();

        // 2. Pull operationIds from the live OpenAPI document.
        var doc = await _client!.GetFromJsonAsync<JsonObject>("/openapi/v1.json");
        doc.Should().NotBeNull();

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = doc!["paths"]?.AsObject();
        paths.Should().NotBeNull();
        foreach (var (_, path) in paths!)
        {
            if (path is not JsonObject methods)
            {
                continue;
            }
            foreach (var (_, op) in methods)
            {
                if (op is JsonObject opObj && opObj["operationId"]?.GetValue<string>() is { } id)
                {
                    operationIds.Add(id);
                }
            }
        }

        // 3. Convention: WithName(...) on the endpoint becomes the operationId. Heuristic
        //    mapping: request "GetFooQuery" / "FooCommand" → operationId "GetFoo" / "Foo".
        var missing = new List<string>();
        foreach (var requestType in requestTypes)
        {
            var expected = StripSuffix(requestType.Name);
            if (!operationIds.Contains(expected) && !operationIds.Any(x => x.Contains(expected, StringComparison.Ordinal)))
            {
                missing.Add($"{requestType.FullName} (expected operationId containing '{expected}')");
            }
        }

        missing.Should().BeEmpty(
            "every MediatR request must have a corresponding HTTP endpoint under /beacon/api/. " +
            "Wrap new handlers in Beacon.SampleProject/Endpoints/{Area}Endpoints.cs.");
    }

    private static bool IsMediatRRequest(Type t)
    {
        return t.GetInterfaces().Any(i =>
            i == typeof(IRequest) ||
            (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));
    }

    private static string StripSuffix(string name)
    {
        foreach (var suffix in new[] { "Command", "Query" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }
        return name;
    }
}
