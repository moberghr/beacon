using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Semantico.Connector.Api.Models;
using Semantico.Core.Models.Providers;

namespace Semantico.Connector.Api.Services;

public class OpenApiImportService(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenApiImportService> logger)
{
    public async Task<List<ApiEndpointMetadata>> ImportAsync(
        string specUrl,
        ApiEndpointFilter? filter,
        CancellationToken ct)
    {
        logger.LogInformation("Importing OpenAPI spec from {SpecUrl}", specUrl);

        var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(specUrl, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return ParseSpec(stream, filter);
    }

    public List<ApiEndpointMetadata> ImportFromString(string specContent, ApiEndpointFilter? filter)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(specContent));
        return ParseSpec(stream, filter);
    }

    private List<ApiEndpointMetadata> ParseSpec(Stream stream, ApiEndpointFilter? filter)
    {
        var document = new OpenApiStreamReader().Read(stream, out var diagnostic);

        if (diagnostic.Errors.Any())
        {
            var errors = string.Join("; ", diagnostic.Errors.Select(e => e.Message));
            logger.LogWarning("OpenAPI spec parsing had errors: {Errors}", errors);
        }

        if (document?.Paths == null)
        {
            logger.LogWarning("OpenAPI spec has no paths");
            return new List<ApiEndpointMetadata>();
        }

        var endpoints = new List<ApiEndpointMetadata>();

        foreach (var pathItem in document.Paths)
        {
            var path = pathItem.Key;

            if (!MatchesFilter(path, filter))
                continue;

            var pathLevelParams = pathItem.Value.Parameters ?? new List<OpenApiParameter>();

            foreach (var operation in pathItem.Value.Operations)
            {
                var method = operation.Key.ToString().ToUpperInvariant();
                var op = operation.Value;

                var tag = op.Tags?.FirstOrDefault()?.Name;

                var allParams = pathLevelParams
                    .Concat(op.Parameters ?? Enumerable.Empty<OpenApiParameter>())
                    .GroupBy(p => (p.Name, p.In))
                    .Select(g => g.Last())
                    .ToList();

                var parameters = allParams.Select(p => new ApiParameterMetadata
                {
                    Name = p.Name,
                    In = p.In?.ToString()?.ToLowerInvariant() ?? "query",
                    Type = GetSchemaType(p.Schema),
                    Required = p.Required,
                    Description = p.Description
                }).ToList();

                var responseFields = ExtractResponseFields(op);

                endpoints.Add(new ApiEndpointMetadata
                {
                    Method = method,
                    Path = path,
                    Summary = op.Summary,
                    Description = op.Description,
                    Tag = tag,
                    Parameters = parameters,
                    ResponseFields = responseFields
                });
            }
        }

        logger.LogInformation("Imported {EndpointCount} endpoints from OpenAPI spec", endpoints.Count);
        return endpoints;
    }

    private static List<ApiResponseFieldMetadata> ExtractResponseFields(OpenApiOperation operation)
    {
        var successResponse = operation.Responses
            .Where(r => r.Key.StartsWith("2"))
            .OrderBy(r => r.Key)
            .Select(r => r.Value)
            .FirstOrDefault();

        if (successResponse == null)
            return new List<ApiResponseFieldMetadata>();

        var content = successResponse.Content
            .Where(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .FirstOrDefault();

        if (content?.Schema == null)
            return new List<ApiResponseFieldMetadata>();

        return FlattenSchema(content.Schema, maxDepth: 2);
    }

    private static List<ApiResponseFieldMetadata> FlattenSchema(OpenApiSchema schema, int maxDepth, int currentDepth = 0, string prefix = "")
    {
        var fields = new List<ApiResponseFieldMetadata>();

        if (currentDepth > maxDepth)
            return fields;

        if (schema.Type == "array" && schema.Items != null)
        {
            return FlattenSchema(schema.Items, maxDepth, currentDepth, prefix);
        }

        if (schema.Properties != null)
        {
            foreach (var prop in schema.Properties)
            {
                var fieldName = string.IsNullOrEmpty(prefix) ? prop.Key : $"{prefix}.{prop.Key}";

                if (prop.Value.Type == "object" && prop.Value.Properties?.Count > 0)
                {
                    fields.AddRange(FlattenSchema(prop.Value, maxDepth, currentDepth + 1, fieldName));
                }
                else
                {
                    fields.Add(new ApiResponseFieldMetadata
                    {
                        Name = fieldName,
                        Type = GetSchemaType(prop.Value),
                        Description = prop.Value.Description
                    });
                }
            }
        }

        return fields;
    }

    private static string GetSchemaType(OpenApiSchema? schema)
    {
        if (schema == null) return "string";
        var type = schema.Type ?? "string";
        if (type == "array") return "array";
        if (type is "integer" or "number") return "number";
        if (type == "boolean") return "boolean";
        if (type == "object") return "object";
        return schema.Format ?? type;
    }

    private static bool MatchesFilter(string path, ApiEndpointFilter? filter)
    {
        if (filter == null)
            return true;

        if (filter.ExcludePathPatterns.Count > 0)
        {
            foreach (var pattern in filter.ExcludePathPatterns)
            {
                if (PathMatchesGlob(path, pattern))
                    return false;
            }
        }

        if (filter.IncludePathPatterns.Count > 0)
        {
            return filter.IncludePathPatterns.Any(pattern => PathMatchesGlob(path, pattern));
        }

        return true;
    }

    private static bool PathMatchesGlob(string path, string pattern)
    {
        path = path.TrimEnd('/');
        pattern = pattern.TrimEnd('/');

        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern[..^3];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*'))
        {
            var prefix = pattern.Split('*')[0];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
