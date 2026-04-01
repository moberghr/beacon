using Semantico.AI.Services.LlmProviders;

namespace Semantico.AI.Services.Mcp;

internal sealed class IntentClassifier : IIntentClassifier
{
    public async Task<IntentClassification> ClassifyAsync(
        ILlmProvider llmProvider, string question, CancellationToken ct)
    {
        var classifyPrompt = """
            Classify the following user question into one of two categories:

            DATA_QUERY — The user wants to retrieve, count, aggregate, or analyze actual data from a database.
            Examples: "How many orders last week?", "Show top customers by revenue", "What's the average response time?", "Create a diagram of usage over 30 days"

            KNOWLEDGE — The user wants to understand how something works, what something is, or learn about the system/project/architecture/processes.
            Examples: "How do notifications work?", "What is the purpose of the subscriptions table?", "Explain the data quality scoring", "What data sources are available?"

            Question: "{question}"

            Respond with exactly one word: DATA_QUERY or KNOWLEDGE
            """;

        var request = new LlmRequest
        {
            SystemPrompt = "You are a question classifier. Respond with exactly one word.",
            Messages = [new ChatMessage(ConversationRole.User, classifyPrompt.Replace("{question}", question))],
            Temperature = 0.0m,
            MaxTokens = 16
        };

        var response = await llmProvider.CompleteAsync(request, ct);
        var result = response.Content.Trim().ToUpperInvariant();

        return result.Contains("KNOWLEDGE") ? IntentClassification.Knowledge : IntentClassification.DataQuery;
    }
}
