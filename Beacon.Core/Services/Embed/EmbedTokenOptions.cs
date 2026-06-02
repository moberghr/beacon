using System.Text;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Embed;

public sealed class EmbedTokenOptions
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "beacon";
    public int DefaultTtlSeconds { get; set; } = 300;
    public int MaxTtlSeconds { get; set; } = 900;
}

internal sealed class EmbedTokenOptionsValidator : IValidateOptions<EmbedTokenOptions>
{
    private const int MinSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, EmbedTokenOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(options.SigningKey))
        {
            failures.Add("Beacon:EmbedToken:SigningKey is required.");
        }
        else if (Encoding.UTF8.GetByteCount(options.SigningKey) < MinSigningKeyBytes)
        {
            failures.Add($"Beacon:EmbedToken:SigningKey must be at least {MinSigningKeyBytes} bytes (UTF-8).");
        }

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Beacon:EmbedToken:Issuer must not be blank.");
        }

        if (options.MaxTtlSeconds <= 0)
        {
            failures.Add("Beacon:EmbedToken:MaxTtlSeconds must be greater than zero.");
        }

        if (options.DefaultTtlSeconds <= 0)
        {
            failures.Add("Beacon:EmbedToken:DefaultTtlSeconds must be greater than zero.");
        }

        if (options.DefaultTtlSeconds > options.MaxTtlSeconds)
        {
            failures.Add("Beacon:EmbedToken:DefaultTtlSeconds must not exceed MaxTtlSeconds.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
