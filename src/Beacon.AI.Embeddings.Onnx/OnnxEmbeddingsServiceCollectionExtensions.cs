using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.AI.Services.Embeddings;

namespace Beacon.AI;

public static class OnnxEmbeddingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local ONNX embedding service, replacing any earlier
    /// <c>IBeaconEmbeddingService</c> registration, including the disabled default of
    /// <c>AddBeaconAI</c>. Works before or after <c>AddBeaconAI</c>. The service reads
    /// <c>Beacon:Embeddings</c> from <c>IConfiguration</c>; it is a singleton so the
    /// inference session is loaded once.
    /// </summary>
    public static IServiceCollection AddBeaconOnnxEmbeddings(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IBeaconEmbeddingService, OnnxEmbeddingService>());
        return services;
    }
}
