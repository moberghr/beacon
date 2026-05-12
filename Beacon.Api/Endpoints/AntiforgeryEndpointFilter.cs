using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Validates the antiforgery token for non-GET/HEAD/OPTIONS requests from
/// authenticated users. Anonymous endpoints opt out via <c>.DisableAntiforgery()</c>.
/// </summary>
internal sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method;

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return await next(context);
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            return Results.Problem(
                title: "Invalid antiforgery token.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}
