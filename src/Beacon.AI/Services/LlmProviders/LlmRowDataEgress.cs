using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>The kinds of row-derived data a prompt can carry to the LLM provider, as the egress log names them.</summary>
public static class LlmRowDataCategories
{
    /// <summary>Stored sample values of columns, shown as examples in a schema context.</summary>
    public const string SampleValues = "sample-values";

    /// <summary>Values that value grounding matched in live data.</summary>
    public const string ValueMatches = "value-matches";

    /// <summary>Distinct column values that documentation generation discovered.</summary>
    public const string ColumnValues = "column-values";

    /// <summary>Rows of query results.</summary>
    public const string ResultRows = "result-rows";

    /// <summary>A database error, redacted, which can still say which value class failed (lesson extraction).</summary>
    public const string DatabaseErrors = "database-errors";

    /// <summary>
    /// Stored documentation of the sections that can list values discovered in the data (business domains, data model,
    /// glossary).
    /// </summary>
    public const string StoredDocumentation = "stored-documentation";
}

/// <summary>
/// What a prompt carrying row-derived data is sent for: the project, the data sources the data was read from and its
/// categories (<see cref="LlmRowDataCategories"/>) — identifiers only. <see cref="CallerId"/> names the caller when the
/// work knows it — a background job names itself, so the record never depends on whom the job runs as; otherwise the
/// current caller's log id (<see cref="BeaconActor.LogId"/>) is used, or <c>unknown</c> when it cannot be resolved.
/// </summary>
public sealed record LlmRowDataScope(int ProjectId, IReadOnlyList<int> DataSourceIds, IReadOnlyList<string> Categories)
{
    public string? CallerId { get; init; }
}

/// <summary>
/// The log of row-derived data handed to the LLM provider: one Information record per request
/// (<see cref="ProjectAccessEvents.RowDataSentToLlm"/>) with the caller, the project, the data sources and the categories,
/// never a value or prompt text (§1.11). Requests still go through the provider handed in, so the queue applies (§6.1). The
/// record is written as the request is handed to the provider, since the data may leave even when the provider then fails;
/// a request the provider fails adds a Warning under the same event with the failure's type and code. A caller that cannot
/// be resolved never fails the request.
/// </summary>
public interface ILlmRowDataEgress
{
    /// <summary>
    /// The provider to send prompts carrying the row-derived data of <paramref name="scope"/> through: every request it
    /// completes is logged, then sent through <paramref name="provider"/>. With no categories, <paramref name="provider"/>
    /// itself.
    /// </summary>
    ILlmProvider Track(ILlmProvider provider, LlmRowDataScope scope);

    /// <summary>
    /// Records row-derived data returned to an MCP client, whose own model reads it (<c>get_query_context</c>): one
    /// Information record under the same event, naming the MCP client as the recipient. Nothing with no categories.
    /// </summary>
    Task RecordReturnedToMcpClientAsync(LlmRowDataScope scope, CancellationToken cancellationToken = default);
}

internal sealed class LlmRowDataEgress(IBeaconActorAccessor actorAccessor, ILogger<LlmRowDataEgress> logger) : ILlmRowDataEgress
{
    public ILlmProvider Track(ILlmProvider provider, LlmRowDataScope scope)
    {
        return scope.Categories.Count == 0
            ? provider
            : new TrackedProvider(provider, scope, this);
    }

    public async Task RecordReturnedToMcpClientAsync(LlmRowDataScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.Categories.Count == 0)
        {
            return;
        }

        var callerId = scope.CallerId ?? await CurrentCallerIdAsync(cancellationToken);

        logger.LogInformation(
            ProjectAccessEvents.RowDataSentToLlm,
            "Grounding context carrying {Categories} read from data sources {DataSourceIds} of project {ProjectId} was returned to the MCP client for caller {CallerId}",
            scope.Categories,
            scope.DataSourceIds,
            scope.ProjectId,
            callerId);
    }

    private async Task<string> LogAsync(LlmRowDataScope scope, CancellationToken cancellationToken)
    {
        var callerId = scope.CallerId ?? await CurrentCallerIdAsync(cancellationToken);

        logger.LogInformation(
            ProjectAccessEvents.RowDataSentToLlm,
            "An LLM request carrying {Categories} read from data sources {DataSourceIds} of project {ProjectId} was handed to the provider for caller {CallerId}",
            scope.Categories,
            scope.DataSourceIds,
            scope.ProjectId,
            callerId);

        return callerId;
    }

    // The outcome of a request the provider failed: the data may have reached it all the same. Type and code only (§1.11).
    private void LogFailure(LlmRowDataScope scope, string callerId, Exception exception)
    {
        logger.LogWarning(
            ProjectAccessEvents.RowDataSentToLlm,
            "The LLM request carrying {Categories} read from data sources {DataSourceIds} of project {ProjectId} for caller {CallerId} failed at the provider ({Failure}); the data may have reached it",
            scope.Categories,
            scope.DataSourceIds,
            scope.ProjectId,
            callerId,
            FailureCodes.Describe(exception));
    }

    // The current caller's log id; a lookup that fails is logged and the request goes on, recorded for "unknown".
    private async Task<string> CurrentCallerIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await actorAccessor.GetCurrentAsync(cancellationToken)).LogId ?? "none";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The caller of an LLM request carrying row data could not be resolved; it is recorded as unknown ({Failure})",
                FailureCodes.Describe(ex));

            return "unknown";
        }
    }

    private sealed class TrackedProvider(ILlmProvider inner, LlmRowDataScope scope, LlmRowDataEgress egress) : ILlmProvider
    {
        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var callerId = await egress.LogAsync(scope, cancellationToken);
            try
            {
                return await inner.CompleteAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                egress.LogFailure(scope, callerId, ex);
                throw;
            }
        }

        public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        {
            return inner.CountTokensAsync(text, cancellationToken);
        }
    }
}
