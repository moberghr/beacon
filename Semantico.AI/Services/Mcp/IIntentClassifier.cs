using Semantico.AI.Services.LlmProviders;

namespace Semantico.AI.Services.Mcp;

public enum IntentClassification
{
    DataQuery,
    Knowledge
}

public interface IIntentClassifier
{
    Task<IntentClassification> ClassifyAsync(ILlmProvider llmProvider, string question, CancellationToken ct);
}
