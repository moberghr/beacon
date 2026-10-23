using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Helpers;
using Beacon.Core.Telemetry;

namespace Beacon.AI.Services.Mcp;

internal sealed class DataSourceRouter(ILogger<DataSourceRouter> logger) : IDataSourceRouter
{
    public async Task<RoutingResult> RouteAsync(
        ILlmProvider llmProvider,
        List<DataSourceKnowledge> dataSources,
        string question,
        CancellationToken ct)
    {
        using var stage = BeaconTelemetry.StartAskStage(BeaconTelemetry.AskStageRouting);
        stage?.SetTag(BeaconTelemetry.AskSourcesTag, dataSources.Count);

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

        // Names, schema names and the question are stored or caller-written text: each name stays on its line and the lot
        // sits in data blocks, so none of it can pose as the prompt's own instructions or answer.
        var summaries = string.Join("\n\n", dataSources.Select(x =>
        {
            var schemas = string.Join(", ", x.Schemas.Select(y => $"{PromptData.OneLine(y.SchemaName)} ({y.TableCount} tables)"));
            return $"Data Source ID: {x.DataSourceId}\nName: {PromptData.OneLine(x.Name)}\nType: {PromptData.OneLine(x.DatabaseEngine ?? x.DataSourceType.ToString())}\nTables: {x.TableCount}\nSchemas: {schemas}";
        }));

        var routingPrompt = $$"""
            You are a data routing expert. Given the following data sources and a user question, determine which data source(s) should be queried.

            DATA SOURCES:
            {{PromptData.Fence("data sources", summaries)}}

            USER QUESTION:
            {{PromptData.Fence("user question", question)}}

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
            SystemPrompt = $"You are a data routing assistant. Respond with JSON only.\n\n{PromptData.Rule}",
            Messages = [new ChatMessage(ConversationRole.User, routingPrompt)],
            Temperature = 0.0m,
            MaxTokens = 512,
            Operation = LlmOperations.AskRouting,
            Tier = LlmTier.Fast
        };

        var response = await BeaconTelemetry.ObserveStageAsync(stage, () => llmProvider.CompleteAsync(request, ct));
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
            // Each source once, in the model's order (its first mention), and no more than RoutingResult.MaxSources: each
            // costs a generation (and possibly repairs) and a query against its database. The rest are reported as dropped.
            var distinctSources = parsed.Sources
                .Where(x => validIds.Contains(x.DatasourceId))
                .DistinctBy(x => x.DatasourceId)
                .ToList();

            if (distinctSources.Count == 0)
            {
                logger.LogWarning("Routing response contained only unknown data source IDs for this project; treating as undetermined");
                return new RoutingResult { Sources = [] };
            }

            if (distinctSources.Count > RoutingResult.MaxSources)
            {
                logger.LogInformation(
                    "Routing named {Count} data sources; more than the {Kept} one ask joins",
                    distinctSources.Count,
                    RoutingResult.MaxSources);
            }

            var legitimateSources = distinctSources
                .Take(RoutingResult.MaxSources)
                .ToList();

            return new RoutingResult
            {
                Sources = legitimateSources.Select(x => new RoutedSource
                {
                    DataSourceId = x.DatasourceId,
                    DataSourceName = dataSources.First(y => y.DataSourceId == x.DatasourceId).Name,
                    Reason = x.Reason ?? ""
                }).ToList(),
                DroppedSourceCount = distinctSources.Count - legitimateSources.Count
            };
        }
        catch (Exception ex)
        {
            // The response's length only, never its text, which can repeat the question (§1.11).
            logger.LogWarning(
                "Failed to parse a routing response of {Length} characters ({Failure})",
                json.Length,
                FailureCodes.Describe(ex));
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
