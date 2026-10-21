using Microsoft.Extensions.Logging;
using Beacon.Core.Models;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Beacon.Connector.Api.Models;
using Beacon.Core.Models.Providers;

namespace Beacon.Connector.Api.Services;

public class OpenApiImportService(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenApiImportService> logger)
{
    // The most an OpenAPI description may hold, and how long fetching it may take.
    private const long MaxSpecBytes = 50 * 1024 * 1024;

    /// <summary>How long fetching the description, body included, may take: 30 seconds. Settable for tests.</summary>
    internal TimeSpan SpecFetchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public async Task<List<ApiEndpointMetadata>> ImportAsync(
        string specUrl,
        ApiEndpointFilter? filter,
        CancellationToken ct)
    {
        logger.LogInformation("Importing OpenAPI spec from {SpecUrl}", specUrl);

        var client = httpClientFactory.CreateClient();
        client.Timeout = SpecFetchTimeout;

        // The deadline covers the body too: HttpClient.Timeout ends once the headers are read.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SpecFetchTimeout);
        MemoryStream stream;
        try
        {
            using var response = await client.GetAsync(specUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            stream = await ApiHttpClientHelper.ReadCappedAsync(response, MaxSpecBytes, "OpenAPI description", timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new BeaconException($"The OpenAPI description was not fetched within {SpecFetchTimeout.TotalSeconds:0} seconds.", ex);
        }

        await using var _ = stream;
        var result = await OpenApiDocument.LoadAsync(stream, null, ReaderSettings(), ct);
        return ToEndpoints(result, filter);
    }

    public List<ApiEndpointMetadata> ImportFromString(string specContent, ApiEndpointFilter? filter)
    {
        var result = OpenApiDocument.Parse(specContent, null, ReaderSettings());
        return ToEndpoints(result, filter);
    }

    // JSON is built in; YAML needs its reader registered. External references stay unloaded, so parsing a
    // description never fetches another document.
    private static OpenApiReaderSettings ReaderSettings()
    {
        var settings = new OpenApiReaderSettings { LoadExternalRefs = false };
        settings.AddYamlReader();
        return settings;
    }

    private List<ApiEndpointMetadata> ToEndpoints(ReadResult result, ApiEndpointFilter? filter)
    {
        var errorCount = result.Diagnostic?.Errors.Count ?? 0;
        if (errorCount > 0)
        {
            // The count only (§1.11): the reader's messages quote the description.
            logger.LogWarning("OpenAPI spec parsing had {ErrorCount} errors", errorCount);
        }

        var document = result.Document;
        if (document?.Paths == null)
        {
            logger.LogWarning("OpenAPI spec has no paths");
            return new List<ApiEndpointMetadata>();
        }

        var endpoints = new List<ApiEndpointMetadata>();

        foreach (var pathItem in document.Paths)
        {
            var path = pathItem.Key;

            if (!MatchesFilter(path, filter) || pathItem.Value.Operations == null)
            {
                continue;
            }

            var pathLevelParams = pathItem.Value.Parameters ?? [];

            foreach (var operation in pathItem.Value.Operations)
            {
                var method = operation.Key.Method.ToUpperInvariant();
                var op = operation.Value;

                var tag = op.Tags?.FirstOrDefault()?.Name;

                var allParams = pathLevelParams
                    .Concat(op.Parameters ?? [])
                    .GroupBy(x => (x.Name, x.In))
                    .Select(x => x.Last())
                    .ToList();

                var parameters = allParams
                    .Select(x =>
                        new ApiParameterMetadata
                        {
                            Name = x.Name ?? string.Empty,
                            In = x.In?.ToString().ToLowerInvariant() ?? "query",
                            Type = GetSchemaType(x.Schema),
                            Required = x.Required,
                            Description = x.Description
                        })
                    .ToList();

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
        var successResponse = (operation.Responses ?? [])
            .Where(x => x.Key.StartsWith('2'))
            .OrderBy(x => x.Key)
            .Select(x => x.Value)
            .FirstOrDefault();

        if (successResponse?.Content == null)
        {
            return new List<ApiResponseFieldMetadata>();
        }

        var content = successResponse.Content
            .Where(x => x.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .FirstOrDefault();

        if (content?.Schema == null)
        {
            return new List<ApiResponseFieldMetadata>();
        }

        return FlattenSchema(content.Schema, maxDepth: 2);
    }

    private static List<ApiResponseFieldMetadata> FlattenSchema(IOpenApiSchema schema, int maxDepth, int currentDepth = 0, string prefix = "")
    {
        var fields = new List<ApiResponseFieldMetadata>();

        if (currentDepth > maxDepth)
        {
            return fields;
        }

        if (TypeOf(schema) == JsonSchemaType.Array && schema.Items != null)
        {
            return FlattenSchema(schema.Items, maxDepth, currentDepth, prefix);
        }

        if (schema.Properties != null)
        {
            foreach (var prop in schema.Properties)
            {
                var fieldName = string.IsNullOrEmpty(prefix) ? prop.Key : $"{prefix}.{prop.Key}";

                if (TypeOf(prop.Value) == JsonSchemaType.Object && prop.Value.Properties?.Count > 0)
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

    private static string GetSchemaType(IOpenApiSchema? schema)
    {
        if (schema == null)
        {
            return "string";
        }

        return TypeOf(schema) switch
        {
            JsonSchemaType.Array => "array",
            JsonSchemaType.Integer or JsonSchemaType.Number => "number",
            JsonSchemaType.Boolean => "boolean",
            JsonSchemaType.Object => "object",
            _ => schema.Format ?? "string"
        };
    }

    // The schema's type without the 3.1 "null" member, so ["string", "null"] reads as the 3.0 nullable string did.
    private static JsonSchemaType? TypeOf(IOpenApiSchema schema) => schema.Type & ~JsonSchemaType.Null;

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
