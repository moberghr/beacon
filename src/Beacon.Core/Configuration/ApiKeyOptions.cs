using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// API-key lifetimes bound from <c>Beacon:ApiKeys</c>. Every new key expires: a key requested without an expiry gets
/// <see cref="DefaultLifetimeDays"/> (capped at the maximum), and one requested to live longer than
/// <see cref="MaxLifetimeDays"/> is refused. Keys issued before these rules keep their stored expiry, and one stored
/// without an expiry keeps working unless <see cref="EnforceMaxLifetimeOnExistingKeys"/> is set.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Beacon:ApiKeys";

    /// <summary>Lifetime of a new key requested without an expiry date.</summary>
    public const int DefaultLifetimeDays = 90;

    /// <summary>Longest lifetime a new key may be issued with, in days. Default 365.</summary>
    public int MaxLifetimeDays { get; set; } = 365;

    /// <summary>
    /// When set, a key stored without an expiry (issued before expiry became mandatory) expires
    /// <see cref="MaxLifetimeDays"/> days after it was created. Off by default: such keys keep working, and each logs a
    /// warning once per process when it is used.
    /// </summary>
    public bool EnforceMaxLifetimeOnExistingKeys { get; set; }
}

internal sealed class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    // About ten years: beyond this the expiry stops limiting anything, and DateTime arithmetic stays far from overflow.
    internal const int UpperBoundDays = 3650;

    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        return options.MaxLifetimeDays is > 0 and <= UpperBoundDays
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ApiKeyOptions.SectionName}:{nameof(ApiKeyOptions.MaxLifetimeDays)} must be between 1 and {UpperBoundDays}.");
    }
}
