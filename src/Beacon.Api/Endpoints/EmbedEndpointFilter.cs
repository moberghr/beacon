using Beacon.Api.Authentication;
using Beacon.Core.Models;
using Beacon.Core.Services.Embed;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Guards the token exchange and render routes: the caller must be the embed principal the embed middleware set for this
/// route's credential (<c>auth_method</c> <see cref="Core.Mcp.McpCallerClaimTypes.EmbedGrantAuthMethod"/> on the exchange,
/// <see cref="Core.Mcp.McpCallerClaimTypes.EmbedTokenAuthMethod"/> on the render), with what it verified. Every refusal is
/// the one generic body: an unavailable grant, lost project access or a refused render is 401, a full render queue or a
/// store that cannot be read 503 with <c>Retry-After</c>; only the exchange's own request errors (a parameter, a lifetime)
/// are told to the host's backend that sent them, as 400. Every answer is <c>Cache-Control: no-store</c>.
/// </summary>
internal sealed class EmbedEndpointFilter(EmbedCredential credential) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<EmbedEndpointFilter>>();
        httpContext.Response.Headers.CacheControl = "no-store";

        if (!EmbedPrincipal.TryRead(httpContext.User, out var grantId, out var presented)
            || presented != credential
            || httpContext.Features.Get<EmbedRequestFeature?>() is not { } feature
            || feature.GrantId != grantId)
        {
            return Unavailable(StatusCodes.Status401Unauthorized);
        }

        try
        {
            return await next(context);
        }
        catch (EmbedRequestInvalidException ex)
        {
            return Results.Json(new { error = "invalid_request", detail = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (EmbedUnavailableException ex)
        {
            logger.LogInformation("Embed request for grant {GrantId} refused: {Reason}", ex.GrantId ?? grantId, ex.Reason);

            return Unavailable(StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (ex is BeaconForbiddenException or BeaconNotFoundException)
        {
            logger.LogInformation("Embed request for grant {GrantId} refused: the grant's creator can no longer run its project's queries", grantId);

            return Unavailable(StatusCodes.Status401Unauthorized);
        }
        catch (EmbedBusyException)
        {
            httpContext.Response.Headers.RetryAfter = "2";

            return Unavailable(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Access that could not be determined, a store that cannot be read: try again, told nothing more.
            logger.LogError(ex, "Embed request for grant {GrantId} could not be served", grantId);
            httpContext.Response.Headers.RetryAfter = "5";

            return Unavailable(StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IResult Unavailable(int statusCode)
    {
        return Results.Json(new { error = EmbedTokenAuthMiddleware.UnavailableError }, statusCode: statusCode);
    }
}
