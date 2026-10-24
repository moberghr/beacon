using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Telemetry;

namespace Beacon.AI.Services.Mcp;

internal sealed class IntentClassifier : IIntentClassifier
{
    public async Task<IntentClassification> ClassifyAsync(
        ILlmProvider llmProvider, string question, CancellationToken ct)
    {
        using var stage = BeaconTelemetry.StartAskStage(BeaconTelemetry.AskStageIntent);

        var classifyPrompt = """
            Classify the following user question into one of two categories:

            DATA_QUERY — The user wants to retrieve, count, aggregate, or analyze actual data from a database.
            Examples: "How many orders last week?", "Show top customers by revenue", "What's the average response time?", "Create a diagram of usage over 30 days"

            KNOWLEDGE — The user wants to understand how something works, what something is, or learn about the system/project/architecture/processes.
            Examples: "How do notifications work?", "What is the purpose of the subscriptions table?", "Explain the data quality scoring", "What data sources are available?"

            Question:
            {question}

            Respond with exactly one word: DATA_QUERY or KNOWLEDGE
            """;

        var request = new LlmRequest
        {
            SystemPrompt = $"You are a question classifier. Respond with exactly one word.\n\n{PromptData.Rule}",
            Messages = [new ChatMessage(ConversationRole.User, classifyPrompt.Replace("{question}", PromptData.Fence("user question", question)))],
            Temperature = 0.0m,
            MaxTokens = 16,
            Operation = LlmOperations.AskIntent,
            Tier = LlmTier.Fast
        };

        var response = await BeaconTelemetry.ObserveStageAsync(stage, () => llmProvider.CompleteAsync(request, ct));
        var result = response.Content.Trim().ToUpperInvariant();

        var intent = result.Contains("KNOWLEDGE") ? IntentClassification.Knowledge : IntentClassification.DataQuery;
        stage?.SetTag(BeaconTelemetry.AskIntentTag, intent.ToString());

        return intent;
    }
}
