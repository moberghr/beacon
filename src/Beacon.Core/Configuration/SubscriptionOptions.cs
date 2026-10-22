using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// How often a subscription may run and how large the file it attaches may be, bound from <c>Beacon:Subscriptions</c>.
/// </summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Beacon:Subscriptions";

    /// <summary>The default of <see cref="MinIntervalMinutes"/>.</summary>
    public const int DefaultMinIntervalMinutes = 5;

    /// <summary>The largest <see cref="MinIntervalMinutes"/> accepted: one day.</summary>
    public const int MaxAllowedMinIntervalMinutes = 1440;

    /// <summary>The default of <see cref="MaxAttachmentBytes"/>: 10 MB.</summary>
    public const int DefaultMaxAttachmentBytes = 10 * 1024 * 1024;

    /// <summary>The smallest <see cref="MaxAttachmentBytes"/> accepted: 64 KB.</summary>
    public const int MinAllowedAttachmentBytes = 64 * 1024;

    /// <summary>The largest <see cref="MaxAttachmentBytes"/> accepted: 100 MB. The file is built in memory.</summary>
    public const int MaxAllowedAttachmentBytes = 100 * 1024 * 1024;

    /// <summary>
    /// The shortest time between two runs of a subscription's schedule. A create or update whose cron fires more often
    /// is refused; a subscription stored before keeps running until it is edited. Default 5; between 1 and
    /// <see cref="MaxAllowedMinIntervalMinutes"/>.
    /// </summary>
    public int MinIntervalMinutes { get; set; } = DefaultMinIntervalMinutes;

    /// <summary>
    /// The largest file a subscription attaches to its delivery. A larger export keeps only the first rows that fit, and
    /// the message says so and how many rows the file holds. Default 10 MB; between
    /// <see cref="MinAllowedAttachmentBytes"/> and <see cref="MaxAllowedAttachmentBytes"/>.
    /// </summary>
    public int MaxAttachmentBytes { get; set; } = DefaultMaxAttachmentBytes;
}

internal sealed class SubscriptionOptionsValidator : IValidateOptions<SubscriptionOptions>
{
    public ValidateOptionsResult Validate(string? name, SubscriptionOptions options)
    {
        var failures = new List<string>();
        if (options.MinIntervalMinutes is < 1 or > SubscriptionOptions.MaxAllowedMinIntervalMinutes)
        {
            failures.Add($"{SubscriptionOptions.SectionName}:{nameof(SubscriptionOptions.MinIntervalMinutes)} must be between 1 and {SubscriptionOptions.MaxAllowedMinIntervalMinutes}.");
        }

        if (options.MaxAttachmentBytes is < SubscriptionOptions.MinAllowedAttachmentBytes or > SubscriptionOptions.MaxAllowedAttachmentBytes)
        {
            failures.Add($"{SubscriptionOptions.SectionName}:{nameof(SubscriptionOptions.MaxAttachmentBytes)} must be between {SubscriptionOptions.MinAllowedAttachmentBytes} and {SubscriptionOptions.MaxAllowedAttachmentBytes}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
