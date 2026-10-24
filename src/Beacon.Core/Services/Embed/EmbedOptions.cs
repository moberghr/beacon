using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// Embedding saved queries in other sites, bound from <c>Beacon:Embed</c>. Off unless the host turns it on: the switch is
/// a deployment lock, so while it is off no embed route is mapped (every one answers 404), no grant is created and no
/// signing key is needed. On, <see cref="SigningKey"/> is required and checked at startup like the encryption key.
/// </summary>
public sealed class EmbedOptions
{
    public const string SectionName = "Beacon:Embed";

    /// <summary>The largest <see cref="MaxTokenTtlSeconds"/> accepted: 15 minutes.</summary>
    public const int MaxAllowedTokenTtlSeconds = 900;

    /// <summary>The largest <see cref="MaxGrantDays"/> accepted.</summary>
    public const int MaxAllowedGrantDays = 366;

    /// <summary>The largest <see cref="MaxOrigins"/> accepted.</summary>
    public const int MaxAllowedOrigins = 50;

    /// <summary>The largest <see cref="MaxRows"/> accepted.</summary>
    public const int MaxAllowedRows = 100_000;

    /// <summary>Maps the embed routes and allows grants to be created. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The key embed tokens are signed with: base64 of at least 32 random bytes (<c>openssl rand -base64 32</c>), never
    /// the encryption key. Required when <see cref="Enabled"/>; changing it invalidates every outstanding token.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long a token lives unless the exchange asks for less. Default 300 seconds.</summary>
    public int TokenTtlSeconds { get; set; } = 300;

    /// <summary>The longest token an exchange may ask for. Default 900 seconds, at most <see cref="MaxAllowedTokenTtlSeconds"/>.</summary>
    public int MaxTokenTtlSeconds { get; set; } = MaxAllowedTokenTtlSeconds;

    /// <summary>The longest a grant may live before it expires. Default 90 days.</summary>
    public int MaxGrantDays { get; set; } = 90;

    /// <summary>The most rows one render returns (also capped by the project's row limit). Default 1,000.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>The most frame origins one grant may list. Default 10.</summary>
    public int MaxOrigins { get; set; } = 10;

    public EmbedRateLimitOptions RateLimits { get; set; } = new();
}

/// <summary>Per-minute limits on the embed routes (<c>Beacon:Embed:RateLimits</c>).</summary>
public sealed class EmbedRateLimitOptions
{
    public int ExchangePerGrantPerMinute { get; set; } = 60;

    public int RenderPerGrantPerMinute { get; set; } = 120;

    public int RefusalsPerIpPerMinute { get; set; } = 30;
}

/// <summary>
/// Checks <see cref="EmbedOptions"/> at startup. While embedding is off nothing is required. On, the signing key must meet
/// the encryption key's requirements (<see cref="EncryptionKeyRequirements"/>) and differ from the encryption key, every
/// previous encryption key and the legacy <c>Beacon:EmbedToken</c> signing key; the messages never contain any key.
/// </summary>
internal sealed class EmbedOptionsValidator(
    string? encryptionKey,
    IEnumerable<string>? previousEncryptionKeys = null,
    string? legacyEmbedTokenKey = null) : IValidateOptions<EmbedOptions>
{
    internal const string SigningKeySetting = EmbedOptions.SectionName + ":" + nameof(EmbedOptions.SigningKey);

    public ValidateOptionsResult Validate(string? name, EmbedOptions options)
    {
        var failures = new List<string>();

        AddWhenOutside(failures, nameof(EmbedOptions.TokenTtlSeconds), options.TokenTtlSeconds, 1, options.MaxTokenTtlSeconds);
        AddWhenOutside(failures, nameof(EmbedOptions.MaxTokenTtlSeconds), options.MaxTokenTtlSeconds, 1, EmbedOptions.MaxAllowedTokenTtlSeconds);
        AddWhenOutside(failures, nameof(EmbedOptions.MaxGrantDays), options.MaxGrantDays, 1, EmbedOptions.MaxAllowedGrantDays);
        AddWhenOutside(failures, nameof(EmbedOptions.MaxRows), options.MaxRows, 1, EmbedOptions.MaxAllowedRows);
        AddWhenOutside(failures, nameof(EmbedOptions.MaxOrigins), options.MaxOrigins, 1, EmbedOptions.MaxAllowedOrigins);
        AddWhenOutside(failures, "RateLimits:" + nameof(EmbedRateLimitOptions.ExchangePerGrantPerMinute), options.RateLimits.ExchangePerGrantPerMinute, 1, 10_000);
        AddWhenOutside(failures, "RateLimits:" + nameof(EmbedRateLimitOptions.RenderPerGrantPerMinute), options.RateLimits.RenderPerGrantPerMinute, 1, 10_000);
        AddWhenOutside(failures, "RateLimits:" + nameof(EmbedRateLimitOptions.RefusalsPerIpPerMinute), options.RateLimits.RefusalsPerIpPerMinute, 1, 10_000);

        if (options.Enabled)
        {
            var keyProblem = EncryptionKeyRequirements.Problem(options.SigningKey, EncryptionKeyRequirements.EmbedSigningKey);
            if (keyProblem != null)
            {
                failures.Add(keyProblem);
            }
            else if (!string.IsNullOrWhiteSpace(encryptionKey)
                && string.Equals(options.SigningKey!.Trim(), encryptionKey.Trim(), StringComparison.Ordinal))
            {
                failures.Add($"{SigningKeySetting} must not be Beacon:EncryptionKey: generate a separate key with `openssl rand -base64 32`.");
            }
            else if ((previousEncryptionKeys ?? []).Any(x => IsSame(options.SigningKey, x)))
            {
                failures.Add($"{SigningKeySetting} must not be one of Beacon:PreviousEncryptionKeys: generate a separate key with `openssl rand -base64 32`.");
            }
            else if (IsSame(options.SigningKey, legacyEmbedTokenKey))
            {
                failures.Add($"{SigningKeySetting} must not be the Beacon:EmbedToken:SigningKey: generate a separate key with `openssl rand -base64 32`.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSame(string? signingKey, string? other)
    {
        return !string.IsNullOrWhiteSpace(other)
            && string.Equals(signingKey!.Trim(), other.Trim(), StringComparison.Ordinal);
    }

    private static void AddWhenOutside(List<string> failures, string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{EmbedOptions.SectionName}:{key} must be between {minimum} and {maximum}.");
        }
    }
}
