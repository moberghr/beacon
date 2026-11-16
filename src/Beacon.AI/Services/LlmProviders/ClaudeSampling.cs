using System.Globalization;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Which Claude models take sampling parameters. Claude Opus 4.7 and later, Sonnet and Haiku 5 and later, Fable and
/// Mythos refuse a request that sets <c>temperature</c> (HTTP 400), so the Claude providers send it only to a model known
/// to take it: Claude 3.x, and Opus, Sonnet and Haiku up to 4.6. Any other model id, a newer or an unrecognised one, gets
/// no temperature and runs at the model's default. Reads the Anthropic API's ids and Bedrock's (a region or
/// <c>global.</c> prefix, <c>anthropic.</c>, a <c>-v1:0</c> suffix, an inference profile ARN).
/// </summary>
public static class ClaudeSampling
{
    private const string Marker = "claude-";

    private static readonly string[] SamplingFamilies = ["opus", "sonnet", "haiku"];

    private static readonly char[] SuffixStart = [':', '@', '/', '['];

    public static bool AcceptsTemperature(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var start = modelId.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var name = modelId[(start + Marker.Length)..];
        var suffix = name.IndexOfAny(SuffixStart);
        var parts = (suffix < 0 ? name : name[..suffix]).Split('-');

        // Claude 3.x and older put the version first: claude-3-5-sonnet-20241022.
        if (VersionPart(parts[0]) is { } legacyMajor)
        {
            return legacyMajor <= 3;
        }

        if (!SamplingFamilies.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts.Length < 2 || VersionPart(parts[1]) is not { } major)
        {
            return false;
        }

        var minor = parts.Length > 2 ? VersionPart(parts[2]) ?? 0 : 0;

        return major < 4 || (major == 4 && minor <= 6);
    }

    // One or two digits. A date (20250929) or a Bedrock version (v1) is not a version part.
    private static int? VersionPart(string part)
    {
        return part.Length is 1 or 2 && part.All(char.IsAsciiDigit)
            ? int.Parse(part, CultureInfo.InvariantCulture)
            : null;
    }
}
