using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Authentication;

/// <summary>
/// Request bounds for Beacon's own routes (<see cref="RequestLimitsOptions"/>): the body size limit on
/// <c>/beacon/api</c> and <c>/beacon/mcp</c>, and a timeout on <c>/beacon/api</c> (the hub and routes marked <see cref="BeaconLongRunningEndpointExtensions.WithBeaconLongRunning{TBuilder}"/> are exempt; the
/// endpoint comes from routing, which runs before this middleware by default). Any other
/// path passes untouched. A limit the host already set lower stays: the body limit is never raised, and a timeout longer than
/// the request's own cancellation changes nothing.
/// </summary>
public sealed class BeaconRequestLimitsMiddleware(RequestDelegate next, IOptions<RequestLimitsOptions> options)
{
    private const string ApiPath = "/beacon/api";
    private const string HubPath = "/beacon/api/hub";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isMcp = path.StartsWithSegments(McpDiscoveryPaths.McpPath, StringComparison.OrdinalIgnoreCase);
        var isApi = path.StartsWithSegments(ApiPath, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments(HubPath, StringComparison.OrdinalIgnoreCase);
        if (!isMcp && !isApi)
        {
            await next(context);
            return;
        }

        var limits = options.Value;
        LimitBody(context, isMcp ? limits.McpMaxBodyBytes : limits.ApiMaxBodyBytes);

        if (!isApi || limits.ApiTimeoutSeconds <= 0 || context.GetEndpoint()?.Metadata.GetMetadata<BeaconLongRunningMetadata>() != null)
        {
            await next(context);
            return;
        }

        var original = context.RequestAborted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(original);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.ApiTimeoutSeconds));
        context.RequestAborted = timeout.Token;

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !original.IsCancellationRequested && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{\"status\":504,\"title\":\"The request took too long.\"}", original);
        }
        finally
        {
            context.RequestAborted = original;
        }
    }

    private static void LimitBody(HttpContext context, long maxBytes)
    {
        if (maxBytes <= 0)
        {
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature == null || feature.IsReadOnly)
        {
            return;
        }

        if (feature.MaxRequestBodySize is not { } current || maxBytes < current)
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }
}
