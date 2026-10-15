using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>Forced metadata refreshes (<c>POST /beacon/api/data-sources/{id}/refresh-metadata</c>), bound from <c>Beacon:Metadata</c>.</summary>
public sealed class MetadataRefreshOptions
{
    public const string SectionName = "Beacon:Metadata";

    /// <summary>The longest cooldown accepted, one day.</summary>
    public const int MaxCooldownMinutes = 1440;

    /// <summary>
    /// After a forced refresh of a data source's metadata succeeded, the next forced refresh of the same data source is
    /// refused (429) for this many minutes, whichever project asks; a failed refresh does not start it, and changing the
    /// data source clears it. Kept per Beacon process. Read at every request, so a changed value applies to the next one.
    /// Default 15; 0 turns the cooldown off.
    /// </summary>
    public int ForcedRefreshCooldownMinutes { get; set; } = 15;
}

internal sealed class MetadataRefreshOptionsValidator : IValidateOptions<MetadataRefreshOptions>
{
    public ValidateOptionsResult Validate(string? name, MetadataRefreshOptions options)
    {
        return options.ForcedRefreshCooldownMinutes is >= 0 and <= MetadataRefreshOptions.MaxCooldownMinutes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{MetadataRefreshOptions.SectionName}:{nameof(MetadataRefreshOptions.ForcedRefreshCooldownMinutes)} must be between 0 and {MetadataRefreshOptions.MaxCooldownMinutes}.");
    }
}
