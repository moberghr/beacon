using Beacon.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Beacon.Api.OpenApi;

public static class BeaconOpenApiExtensions
{
    /// <summary>The setting that publishes the OpenAPI document to anonymous callers (<c>Beacon:OpenApi:Anonymous</c>).</summary>
    public const string AnonymousSettingName = "Beacon:OpenApi:Anonymous";

    /// <summary>
    /// Maps the OpenAPI document (<c>/openapi/v1.json</c>) like <c>MapOpenApi()</c>, but outside Development it needs a
    /// signed-in Beacon caller (the <c>BeaconApi</c> policy) unless the host sets <c>Beacon:OpenApi:Anonymous</c> to true.
    /// The Beacon session cookie and API keys authenticate only <c>/beacon</c> and the prefixes in
    /// <c>Beacon:Authentication:AdditionalPaths</c>, so a host that gates the document lists <c>/openapi</c> there. Code
    /// generation runs in Development, where the document stays open.
    /// </summary>
    public static IEndpointConventionBuilder MapBeaconOpenApi(this WebApplication app)
    {
        var builder = app.MapOpenApi();
        var anonymous = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>(AnonymousSettingName);
        if (anonymous)
        {
            // Marked anonymous, so the login redirect leaves it alone where /openapi is one of Beacon's paths.
            builder.AllowAnonymous();
            return builder;
        }

        builder.RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);
        return builder;
    }
}
