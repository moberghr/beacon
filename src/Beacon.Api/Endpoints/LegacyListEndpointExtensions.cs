using Beacon.Core.Helpers;

namespace Beacon.Api.Endpoints;

internal static class LegacyListEndpointExtensions
{
    /// <summary>
    /// Marks a pre-paging list route deprecated in the OpenAPI document (pointing at its paged twin) and says in
    /// <c>X-Beacon-Truncated: true</c> when the safety cap cut its answer.
    /// </summary>
    public static RouteHandlerBuilder AsDeprecatedUnpagedList(this RouteHandlerBuilder builder, string pagedRoute)
    {
        return builder
            .WithMetadata(new ObsoleteAttribute($"Unpaged and cut at {ListRequest.LegacyUnpagedCap} rows; use {pagedRoute}."))
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                if (context.HttpContext.RequestServices.GetService<LegacyListTruncation>()?.IsTruncated == true)
                {
                    context.HttpContext.Response.Headers[LegacyListTruncation.HeaderName] = "true";
                }

                return result;
            });
    }
}
