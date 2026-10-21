using Beacon.AI.Services.Ai.AiActor;

namespace Beacon.Tests.Common;

/// <summary>A think claim that always succeeds, for tests whose mocked context cannot run a bulk update.</summary>
public sealed class GrantingThinkClaim : IAiActorThinkClaim
{
    public Task<ThinkClaimResult> TryClaimAsync(int actorId, DateTime now, DateTime threshold, CancellationToken cancellationToken)
        => Task.FromResult(ThinkClaimResult.Claimed);
}
