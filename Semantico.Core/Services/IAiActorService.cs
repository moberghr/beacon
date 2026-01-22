namespace Semantico.Core.Services;

/// <summary>
/// Interface for AI Actor service. Implementation is provided by Semantico.AI package.
/// This interface allows Core to optionally depend on AI functionality without requiring it.
/// </summary>
public interface IAiActorService
{
    /// <summary>
    /// Called when a subscription is executed, allowing AI actors to respond to data changes.
    /// </summary>
    Task OnSubscriptionExecutedAsync(int subscriptionId, int rowCount, CancellationToken cancellationToken);
}
