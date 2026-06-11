using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Mcp;

internal sealed class DataSourceRouter(ILogger<DataSourceRouter> logger) : IDataSourceRouter
{
    public async Task<RoutingResult> RouteAsync(
        ILlmProvider llmProvider,
        List<DataSourceKnowledge> dataSources,
        string question,
        CancellationToken ct)
    {
        if (dataSources.Count == 1)
        {
            return new RoutingResult
            {
                Sources =
                [
                    new RoutedSource
                    {
                        DataSourceId = dataSources[0].DataSourceId,
                        DataSourceName = dataSources[0].Name,
                        Reason = "Only data source in this project."
                    }
                ]
            };
        }

        var summaries = string.Join("\n\n", dataSources.Select(x =>
        {
            var schemas = string.Join(", ", x.Schemas.Select(y => $"{y.SchemaName} ({y.TableCount} tables)"));
            return $"Data Source ID: {x.DataSourceId}\nName: {x.Name}\nType: {x.DatabaseEngine ?? x.DataSourceType.ToString()}\nTables: {x.TableCount}\nSchemas: {schemas}";
        }));

        var routingPrompt = $$"""
            You are a data routing expert. Given the following data sources and a user question, determine which data source(s) should be queried.

            DATA SOURCES:
            {{summaries}}

            USER QUESTION: {{question}}

            Respond with JSON only, no markdown:
            {
              "sources": [
                { "datasource_id": <id>, "datasource_name": "<name>", "reason": "<why this source>" }
              ]
            }

            Rules:
            - Pick the minimum number of sources needed to answer the question.
            - Most questions need only one source.
            - Only use multiple sources if the question explicitly involves data from different domains/systems.
            """;

        var request = new LlmRequest
        {
            SystemPrompt = "You are a data routing assistant. Respond with JSON only.",
            Messages = [new ChatMessage(ConversationRole.User, routingPrompt)],
            Temperature = 0.0m,
            MaxTokens = 512
        };

        var response = await llmProvider.CompleteAsync(request, ct);
        var json = response.Content.Trim();
        if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<RoutingResponse>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (parsed?.Sources == null || parsed.Sources.Count == 0)
            {
                return new RoutingResult
                {
                    Sources = [new RoutedSource
                    {
                        DataSourceId = dataSources[0].DataSourceId,
                        DataSourceName = dataSources[0].Name,
                        Reason = "Default selection (routing could not determine specific source)."
                    }]
                };
            }

            // Only accept IDs that belong to this project's data sources — the LLM output is
            // untrusted and must never route to another project's sources.
            var validIds = dataSources
                .Select(x => x.DataSourceId)
                .ToHashSet();
            var legitimateSources = parsed.Sources
                .Where(x => validIds.Contains(x.DatasourceId))
                .ToList();

            if (legitimateSources.Count == 0)
            {
                logger.LogWarning("Routing response contained only unknown data source IDs for this project; treating as undetermined");
                return new RoutingResult { Sources = [] };
            }

            return new RoutingResult
            {
                Sources = legitimateSources.Select(x => new RoutedSource
                {
                    DataSourceId = x.DatasourceId,
                    DataSourceName = dataSources.First(y => y.DataSourceId == x.DatasourceId).Name,
                    Reason = x.Reason ?? ""
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse routing response: {Json}", json);
            return new RoutingResult
            {
                Sources = [new RoutedSource
                {
                    DataSourceId = dataSources[0].DataSourceId,
                    DataSourceName = dataSources[0].Name,
                    Reason = "Default selection (routing parse error)."
                }]
            };
        }
    }
}
