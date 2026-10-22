using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>AI actor think cycles, bound from <c>Beacon:AiActors</c>.</summary>
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
}

internal sealed class AiActorOptionsValidator : IValidateOptions<AiActorOptions>
{
    public ValidateOptionsResult Validate(string? name, AiActorOptions options)
    {
        return options.ThinkCooldownMinutes is >= 0 and <= AiActorOptions.MaxThinkCooldownMinutes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AiActorOptions.SectionName}:{nameof(AiActorOptions.ThinkCooldownMinutes)} must be between 0 and {AiActorOptions.MaxThinkCooldownMinutes}.");
    }
}
