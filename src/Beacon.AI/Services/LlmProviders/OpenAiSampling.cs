namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Which OpenAI models take sampling parameters. The reasoning models (o-series, GPT-5 and later) refuse a request
/// that sets a <c>temperature</c> other than their default (HTTP 400), so the OpenAI provider sends it only to a model
/// known to take it: the GPT-3.5 and GPT-4 families (<c>gpt-4o</c>, <c>gpt-4.1</c>, ...), also as a fine-tune
/// (<c>ft:gpt-4o-mini:org::id</c>). Any other model id, a newer or an unrecognised one, gets no temperature and runs at
/// the model's default. An Azure deployment has a free name, so there the question is the other one: whether the name
/// says it is a reasoning model (<see cref="NamesReasoningModel"/>).
/// </summary>
public static class OpenAiSampling
{
    private const string FineTunePrefix = "ft:";

    private static readonly string[] SamplingFamilies = ["gpt-4", "gpt-3.5"];

    private static readonly char[] Separators = ['-', '_', ' ', '/', ':'];

    public static bool AcceptsTemperature(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var name = modelId.Trim();
        if (name.StartsWith(FineTunePrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[FineTunePrefix.Length..];
        }

        return SamplingFamilies.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a free-form name (an Azure deployment) contains a reasoning model's name: <c>gpt-5</c> or a later major
    /// version, or an o-series model (<c>o1</c>, <c>o3-mini</c>, <c>o4-mini</c>) as a whole part of the name.
    /// <c>my-gpt-4o</c> and <c>prod-chat</c> do not.
    /// </summary>
    public static bool NamesReasoningModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var parts = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 2 && part[0] is 'o' or 'O' && part[1] is '1' or '3' or '4')
            {
                return true;
            }

            var version = part.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) && part.Length > 3
                ? part[3..]
                : string.Equals(part, "gpt", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length ? parts[i + 1] : null;
            if (version != null && MajorVersion(version) >= 5)
            {
                return true;
            }
        }

        return false;
    }

    // The leading digits of "6.2", "5" or "4o"; none is no version. Azure writes GPT-3.5 as "35".
    private static int MajorVersion(string version)
    {
        var digits = new string(version.TakeWhile(char.IsAsciiDigit).Take(3).ToArray());
        if (digits.Length == 0 || digits == "35")
        {
            return 0;
        }

        return int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }
}
