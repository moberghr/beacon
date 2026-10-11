using Beacon.Api.Authentication;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// One <c>/beacon/api</c> route as <c>MapBeaconApi</c> maps it: method, pattern, endpoint name, policies, the
/// functional permissions the permission filter admits on it (<see cref="PermissionRequired"/>: as its requirement), and
/// whether the filter admits only an Admin by live roles (<see cref="RequiresLiveAdmin"/>).
/// </summary>
internal sealed record BeaconApiRoute(
    string Method,
    string Pattern,
    string? Name,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> AdmittedPermissions,
    bool PermissionRequired,
    bool RequiresLiveAdmin = false)
{
    /// <summary>The route as policy maps name it: <c>METHOD /pattern</c>.</summary>
    public string Key => $"{Method} {Pattern}";

    /// <summary>A route that addresses something by a route value (<c>{id}</c>, <c>{projectId}</c>, …).</summary>
    public bool IsById => Pattern.Contains('{');

    public override string ToString() => $"{Method} {Pattern} ({Name})";
}

/// <summary>
/// Every route <c>MapBeaconApi</c> maps, read from the endpoint data sources without starting a server, and the MediatR
/// request each one sends (its endpoint name is the request's name without <c>Command</c>/<c>Query</c>).
/// </summary>
internal static class BeaconApiRoutes
{
    public static async Task<IReadOnlyList<BeaconApiRoute>> ReadAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(Mock.Of<IApiKeyService>());
        builder.Services.AddSingleton(new BeaconConfiguration());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthenticationProvider>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>());
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IRoleService>());
        builder.Services.AddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddAntiforgery();
        builder.Services.AddRouting();
        builder.Services.AddBeaconApiAuthorization();
        await using var app = builder.Build();
        app.MapBeaconApi();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/beacon/api/", StringComparison.Ordinal) == true)
            .SelectMany(x => (x.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(y =>
                    new BeaconApiRoute(
                        y,
                        x.RoutePattern.RawText!,
                        x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                        x.Metadata.GetOrderedMetadata<IAuthorizeData>()
                            .Select(z => z.Policy)
                            .OfType<string>()
                            .ToList(),
                        x.Metadata.GetMetadata<BeaconFunctionalPermissionMetadata>()?.Permissions ?? [],
                        x.Metadata.GetMetadata<BeaconFunctionalPermissionMetadata>()?.IsRequired == true,
                        x.Metadata.GetMetadata<BeaconAdminMetadata>() != null)))
            .ToList();
    }

    /// <summary>Every MediatR request in the Beacon handler assemblies.</summary>
    public static IReadOnlyList<Type> RequestTypes()
    {
        return new[]
            {
                typeof(Beacon.Core.Handlers.Projects.GetProjectsQuery).Assembly,
                typeof(Beacon.AI.ServiceConfiguration).Assembly,
                typeof(Beacon.MCP.ServiceConfiguration).Assembly
            }
            .Distinct()
            .SelectMany(x => x.GetTypes())
            .Where(x => x is { IsAbstract: false, IsInterface: false })
            .Where(x => x.GetInterfaces().Any(y => y == typeof(IRequest) || (y.IsGenericType && y.GetGenericTypeDefinition() == typeof(IRequest<>))))
            .OrderBy(x => x.FullName)
            .ToList();
    }

    /// <summary>The request a route sends, by its endpoint name; null when no request has that name.</summary>
    public static Type? RequestOf(BeaconApiRoute route, IReadOnlyList<Type> requests)
    {
        return route.Name == null
            ? null
            : requests.FirstOrDefault(x => StripSuffix(x.Name) == route.Name);
    }

    public static string StripSuffix(string name)
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

    /// <summary>Whether the request is exempt from project authorization as a whole.</summary>
    public static ProjectScopeExemptAttribute? ExemptionOf(Type request)
    {
        return (ProjectScopeExemptAttribute?)Attribute.GetCustomAttribute(request, typeof(ProjectScopeExemptAttribute), inherit: false);
    }
}
