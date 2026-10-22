using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>AI actor think cycles and plans, bound from <c>Beacon:AiActors</c>.</summary>
public sealed class AiActorOptions
{
    public const string SectionName = "Beacon:AiActors";

    /// <summary>The longest cooldown accepted, one day.</summary>
    public const int MaxThinkCooldownMinutes = 1440;

    /// <summary>
    /// An actor's think cycle starts at most once per this many minutes, counted from the actor's last think time, whether
    /// a subscription run enqueued it or a user asked for it. A skipped background cycle is only logged; a requested one is
    /// answered with the time left. Read at every cycle, so a changed value applies to the next one.
    /// Default 15; 0 turns the cooldown off.
    /// </summary>
    public int ThinkCooldownMinutes { get; set; } = 15;

    /// <summary>The longest plan expiry accepted, one year.</summary>
    public const int MaxPlanExpiryDays = 365;

    /// <summary>
    /// A plan waiting for approval expires this many days after it was proposed: it is no longer listed as pending, and
    /// approving it or asking for its revision is refused as for a plan decided already. Computed when read, so a changed
    /// value applies to plans already proposed. Default 7; between 1 and <see cref="MaxPlanExpiryDays"/>.
    /// </summary>
    public int PlanExpiryDays { get; set; } = 7;

    /// <summary>When a plan proposed at <paramref name="proposedAt"/> expires; from then on it is no longer pending.</summary>
    public DateTime PlanExpiresAt(DateTime proposedAt) => proposedAt.AddDays(PlanExpiryDays);
}

internal sealed class AiActorOptionsValidator : IValidateOptions<AiActorOptions>
{
    public ValidateOptionsResult Validate(string? name, AiActorOptions options)
    {
        if (options.ThinkCooldownMinutes is < 0 or > AiActorOptions.MaxThinkCooldownMinutes)
        {
            return ValidateOptionsResult.Fail(
                $"{AiActorOptions.SectionName}:{nameof(AiActorOptions.ThinkCooldownMinutes)} must be between 0 and {AiActorOptions.MaxThinkCooldownMinutes}.");
        }

        return options.PlanExpiryDays is >= 1 and <= AiActorOptions.MaxPlanExpiryDays
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AiActorOptions.SectionName}:{nameof(AiActorOptions.PlanExpiryDays)} must be between 1 and {AiActorOptions.MaxPlanExpiryDays}.");
    }
}
