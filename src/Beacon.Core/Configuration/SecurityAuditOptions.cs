using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>The security audit trail (<c>Beacon:Audit</c>): its integrity key and how long its events are kept.</summary>
public sealed class SecurityAuditOptions
{
    public const string SectionName = "Beacon:Audit";

    /// <summary>The shortest retention accepted: one year.</summary>
    public const int MinRetentionDays = 365;

    /// <summary>The longest retention accepted: one hundred years.</summary>
    public const int MaxRetentionDays = 36500;

    /// <summary>The shortest integrity key accepted.</summary>
    public const int MinIntegrityKeyLength = 32;

    /// <summary>
    /// The secret each event's integrity hash is keyed with (at least 32 characters). Unset, Beacon derives the key, for this
    /// purpose only, from <c>Beacon:Mcp:CallerHashKey</c>, else from <c>Beacon:EncryptionKey</c>. Set it before the
    /// encryption key is changed, so events written from then on verify under a key that does not change with it.
    /// </summary>
    public string? IntegrityKey { get; set; }

    /// <summary>
    /// Integrity keys used earlier, accepted when events are verified (never used to write). Events written under a key
    /// derived from the current or a previous encryption key (<c>Beacon:PreviousEncryptionKeys</c>) verify as well.
    /// </summary>
    public List<string> PreviousIntegrityKeys { get; set; } = [];

    /// <summary>
    /// How many days events are kept; null (the default) keeps them forever. At least 365. Events older than this are
    /// deleted by the security audit retention job, which the host schedules, and the deletion is itself recorded.
    /// </summary>
    public int? RetentionDays { get; set; }
}

internal sealed class SecurityAuditOptionsValidator : IValidateOptions<SecurityAuditOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityAuditOptions options)
    {
        var failures = new List<string>();
        if (options.RetentionDays is { } days && days is < SecurityAuditOptions.MinRetentionDays or > SecurityAuditOptions.MaxRetentionDays)
        {
            failures.Add(
                $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.RetentionDays)} must be between {SecurityAuditOptions.MinRetentionDays} and {SecurityAuditOptions.MaxRetentionDays} days, or unset to keep events forever.");
        }

        var keyProblem = IntegrityKeyProblem(options.IntegrityKey, nameof(SecurityAuditOptions.IntegrityKey));
        if (keyProblem != null)
        {
            failures.Add(keyProblem);
        }

        foreach (var previous in options.PreviousIntegrityKeys)
        {
            var previousProblem = IntegrityKeyProblem(previous, nameof(SecurityAuditOptions.PreviousIntegrityKeys));
            if (previousProblem != null)
            {
                failures.Add(previousProblem);
                break;
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Why <paramref name="key"/> is no integrity key; null for a usable key or none. Never contains the key.</summary>
    internal static string? IntegrityKeyProblem(string? key, string setting)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Contains("${", StringComparison.Ordinal))
        {
            return $"{SecurityAuditOptions.SectionName}:{setting} contains '${{': .NET configuration does not expand placeholders, so the key would be the placeholder text itself.";
        }

        return key.Trim().Length < SecurityAuditOptions.MinIntegrityKeyLength
            ? $"{SecurityAuditOptions.SectionName}:{setting} must be at least {SecurityAuditOptions.MinIntegrityKeyLength} characters."
            : null;
    }
}
