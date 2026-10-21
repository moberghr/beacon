using Beacon.AI.Services.LlmProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.AI;

public static class BedrockServiceCollectionExtensions
{
    /// <summary>
    /// Installs the Amazon Bedrock LLM provider, so the Bedrock setting in Admin Settings can be activated. Works before
    /// or after <c>AddBeaconAI</c>; calling it more than once registers it once.
    /// </summary>
    public static IServiceCollection AddBeaconBedrock(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderRegistration, BedrockRegistration>());
        return services;
    }
}
