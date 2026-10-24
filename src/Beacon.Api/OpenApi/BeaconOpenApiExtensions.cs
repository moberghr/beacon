using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.OpenApi;

public static class BeaconOpenApiExtensions
{
    /// <summary>
    /// Adds the OpenAPI document (<c>v1</c>) Beacon's React shell generates its typed client from; the host serves it with
    /// <c>app.MapOpenApi()</c> at <c>/openapi/v1.json</c>. Enums stay integers on the wire: each enum becomes a component
    /// schema named after its type, carrying its values and names (<see cref="EnumSchemaTransformer"/>), so the generated
    /// TypeScript client has the backend's enum values. <paramref name="configure"/> runs after Beacon's settings.
    /// </summary>
    public static IServiceCollection AddBeaconOpenApi(this IServiceCollection services, Action<OpenApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenApi(x =>
        {
            x.CreateSchemaReferenceId = y =>
            {
                var type = Nullable.GetUnderlyingType(y.Type) ?? y.Type;
                return type.IsEnum ? type.Name : OpenApiOptions.CreateDefaultSchemaReferenceId(y);
            };
            x.AddSchemaTransformer<EnumSchemaTransformer>();
            configure?.Invoke(x);
        });
    }
}
