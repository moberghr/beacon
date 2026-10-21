using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// The LLM providers this host can build. Beacon.AI supplies the installed ones; without it AI is off and none are.
/// </summary>
public interface ILlmProviderCatalog
{
    /// <summary>Whether an AI package supplies this catalog. False: settings are only stored, nothing is built.</summary>
    bool AiEnabled { get; }

    /// <summary>The installed providers, in <see cref="AiProvider"/> order.</summary>
    IReadOnlyList<LlmProviderInfo> Registered { get; }
}

public record LlmProviderInfo(AiProvider Provider, bool RequiresApiKey);
