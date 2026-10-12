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

    /// <summary>
    /// When set, a key stops working while its owner has not signed in to Beacon with their own identity (a password, a
    /// single sign-on or an identity-provider token, never an API key) for this many days, counted from the owner's last
    /// sign-in or, for an owner who never signed in, from the account's creation. It works again after the owner signs in.
    /// Off by default (0); at most <see cref="ApiKeyOptionsValidator.UpperBoundDays"/>.
    /// </summary>
    public int MaxOwnerInactivityDays { get; set; }
}

internal sealed class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    // About ten years: beyond this the expiry stops limiting anything, and DateTime arithmetic stays far from overflow.
    internal const int UpperBoundDays = 3650;

    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        var failures = new List<string>();
        if (options.MaxLifetimeDays is <= 0 or > UpperBoundDays)
        {
            failures.Add($"{ApiKeyOptions.SectionName}:{nameof(ApiKeyOptions.MaxLifetimeDays)} must be between 1 and {UpperBoundDays}.");
        }

        if (options.MaxOwnerInactivityDays is < 0 or > UpperBoundDays)
        {
            failures.Add($"{ApiKeyOptions.SectionName}:{nameof(ApiKeyOptions.MaxOwnerInactivityDays)} must be between 0 (off) and {UpperBoundDays}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
