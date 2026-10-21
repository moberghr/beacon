using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Microsoft.Extensions.Logging;

namespace Beacon.Tests.Common;

/// <summary>A factory with every provider installed: the built-in registrations plus Bedrock.</summary>
internal static class TestLlmProviderFactory
{
    public static IReadOnlyList<ILlmProviderRegistration> AllRegistrations(ILogger<BedrockRegistration>? bedrockLogger = null)
    {
        return [.. BuiltInLlmProviderRegistrations.Create(), new BedrockRegistration(bedrockLogger)];
    }

    public static LlmProviderFactory Create(Func<LlmConfiguration> configAccessor, ILogger<BedrockRegistration>? bedrockLogger = null)
    {
        return new LlmProviderFactory(configAccessor, AllRegistrations(bedrockLogger));
    }
}
