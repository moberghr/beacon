using System.Diagnostics;
using Beacon.Core.Models.Ai;
using Beacon.Core.Telemetry;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The span and metrics of one LLM completion (<see cref="BeaconTelemetry.LlmCompletionActivityName"/>,
/// <see cref="BeaconTelemetry.LlmCalls"/>, <see cref="BeaconTelemetry.LlmTokens"/>). Tagged with the provider kind, the
/// configured model, the request's operation name, token counts and the outcome only — never the prompt, the completion,
/// SQL or a question (§1.11).
/// </summary>
internal static class LlmCompletionTelemetry
{
    // A model name the provider reports back is tagged only when it looks like one: short, and only the characters model
    // and deployment names use. Anything else stays off the span.
    private const int MaxResponseModelLength = 128;

    /// <summary>
    /// Traces <paramref name="complete"/>. The provider kind and model are the caller's, read from the same snapshot as the
    /// provider that serves the call.
    /// </summary>
    public static async Task<LlmResponse> TraceAsync(
        string providerKind,
        string? requestModel,
        LlmRequest request,
        Func<Task<LlmResponse>> complete)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrWhiteSpace(requestModel) ? null : requestModel;

        using var activity = BeaconTelemetry.Source.StartActivity(BeaconTelemetry.LlmCompletionActivityName, ActivityKind.Client);
        activity?.SetTag(BeaconTelemetry.LlmProviderTag, providerKind);
        activity?.SetTag(BeaconTelemetry.LlmRequestModelTag, model);
        activity?.SetTag(BeaconTelemetry.LlmOperationTag, request.Operation);
        activity?.SetTag(BeaconTelemetry.LlmCacheHintTag, request.Messages?.Any(x => x?.CacheBreakpoint == true) == true);

        var tags = new TagList
        {
            { BeaconTelemetry.LlmProviderTag, providerKind }
        };
        if (model != null)
        {
            tags.Add(BeaconTelemetry.LlmRequestModelTag, model);
        }

        if (request.Operation != null)
        {
            tags.Add(BeaconTelemetry.LlmOperationTag, request.Operation);
        }

        try
        {
            var response = await complete();

            activity?.SetTag(BeaconTelemetry.LlmResponseModelTag, IsModelName(response.Model) ? response.Model : null);
            activity?.SetTag(BeaconTelemetry.LlmInputTokensTag, response.InputTokens);
            activity?.SetTag(BeaconTelemetry.LlmOutputTokensTag, response.OutputTokens);
            activity?.SetTag(BeaconTelemetry.LlmCacheReadTokensTag, response.CacheReadInputTokens);
            activity?.SetTag(BeaconTelemetry.LlmCacheWriteTokensTag, response.CacheWriteInputTokens);
            activity?.SetTag(BeaconTelemetry.LlmTruncatedTag, response.Truncated);
            activity?.SetTag(BeaconTelemetry.OutcomeTag, BeaconTelemetry.OutcomeSuccess);

            RecordTokens(tags, BeaconTelemetry.TokenTypeInput, response.InputTokens);
            RecordTokens(tags, BeaconTelemetry.TokenTypeOutput, response.OutputTokens);
            RecordTokens(tags, BeaconTelemetry.TokenTypeCacheRead, response.CacheReadInputTokens);
            RecordTokens(tags, BeaconTelemetry.TokenTypeCacheWrite, response.CacheWriteInputTokens);

            tags.Add(BeaconTelemetry.OutcomeTag, BeaconTelemetry.OutcomeSuccess);
            BeaconTelemetry.LlmCalls.Add(1, tags);

            return response;
        }
        catch (Exception ex)
        {
            // Type names only: a provider message can quote the request. A cancellation is not an error.
            BeaconTelemetry.RecordFailure(activity, ex);

            if (ex is OperationCanceledException)
            {
                tags.Add(BeaconTelemetry.OutcomeTag, BeaconTelemetry.OutcomeCancelled);
            }
            else
            {
                tags.Add(BeaconTelemetry.OutcomeTag, BeaconTelemetry.OutcomeError);
                tags.Add(BeaconTelemetry.ErrorTypeTag, ex.GetType().Name);
            }

            BeaconTelemetry.LlmCalls.Add(1, tags);

            throw;
        }
    }

    private static void RecordTokens(TagList tags, string tokenType, int count)
    {
        // A provider that does not report a count (no prompt cache, an estimate of zero) records nothing for it.
        if (count <= 0)
        {
            return;
        }

        tags.Add(BeaconTelemetry.LlmTokenTypeTag, tokenType);
        BeaconTelemetry.LlmTokens.Record(count, tags);
    }

    private static bool IsModelName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxResponseModelLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':' or '/' or '@'))
            {
                return false;
            }
        }

        return true;
    }
}
