using System.Globalization;
using Beacon.Core.Models;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Maps the access outcomes Beacon's handlers throw to RFC 7807 responses on every <c>/beacon/api</c> endpoint, whatever
/// exception handling the host adds: <see cref="BeaconForbiddenException"/> → 403 and
/// <see cref="BeaconNotFoundException"/> → 404, both without the exception's message (a caller cannot tell a resource it
/// may not see from a missing one); <see cref="BeaconConflictException"/> → 409 and
/// <see cref="BeaconRateLimitedException"/> → 429 and <see cref="BeaconValidationException"/> → 400 with theirs. Only these Beacon exceptions: any other exception (an
/// <see cref="UnauthorizedAccessException"/> from file access, say) is left to the host's exception handling. Each
/// mapping is logged with the exception type, the route name and the route's ids only. The outermost filter of the
/// group, so it also covers the filters after it.
/// </summary>
internal sealed class BeaconAccessExceptionEndpointFilter(ILogger<BeaconAccessExceptionEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BeaconForbiddenException ex)
        {
            return Mapped(context, ex, StatusCodes.Status403Forbidden, "Forbidden");
        }
        catch (BeaconNotFoundException ex)
        {
            return Mapped(context, ex, StatusCodes.Status404NotFound, "Not Found");
        }
        catch (BeaconValidationException ex)
        {
            return Mapped(context, ex, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (BeaconConflictException ex)
        {
            return Mapped(context, ex, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (BeaconRateLimitedException ex)
        {
            return Mapped(context, ex, StatusCodes.Status429TooManyRequests, ex.Message);
        }
    }

    private IResult Mapped(EndpointFilterInvocationContext context, Exception exception, int statusCode, string title)
    {
        var httpContext = context.HttpContext;
        var routeName = httpContext.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        var routeIds = httpContext.Request.RouteValues
            .Where(x => int.TryParse(x.Value?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(x => $"{x.Key}={x.Value}")
            .ToList();

        logger.LogInformation(
            "Mapped {ExceptionType} to HTTP {StatusCode} on route {RouteName} ({RouteIds})",
            exception.GetType().Name,
            statusCode,
            routeName ?? "unnamed",
            string.Join(",", routeIds));

        return Results.Problem(title: title, statusCode: statusCode);
    }
}
