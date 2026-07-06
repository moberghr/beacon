using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Beacon.Api.OpenApi;

/// <summary>
/// Enums serialize as raw integers, which the default OpenAPI exporter emits as bare
/// <c>type: integer</c> schemas. This transformer adds the member values and
/// <c>x-enumNames</c> so NSwag generates named TypeScript enums with the exact backend
/// values — the UI must never hand-write enum mappings.
/// </summary>
public sealed class EnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (!type.IsEnum)
        {
            return Task.CompletedTask;
        }

        schema.Enum.Clear();
        var names = new OpenApiArray();
        foreach (var name in Enum.GetNames(type))
        {
            schema.Enum.Add(new OpenApiInteger(Convert.ToInt32(Enum.Parse(type, name))));
            names.Add(new OpenApiString(name));
        }

        schema.Extensions["x-enumNames"] = names;
        return Task.CompletedTask;
    }
}
