using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Mcp;

public enum IntentClassification
{
    DataQuery,
    Knowledge
}

public interface IIntentClassifier
{
    Task<IntentClassification> ClassifyAsync(ILlmProvider llmProvider, string question, CancellationToken ct);
}
