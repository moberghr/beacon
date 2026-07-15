using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

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

        var values = new List<JsonNode>();
        var names = new JsonArray();
        foreach (var name in Enum.GetNames(type))
        {
            values.Add(JsonValue.Create(Convert.ToInt32(Enum.Parse(type, name))));
            names.Add(JsonValue.Create(name));
        }

        schema.Enum = values;
        schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        schema.Extensions["x-enumNames"] = new JsonNodeExtension(names);
        return Task.CompletedTask;
    }
}
