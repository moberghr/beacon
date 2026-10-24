using Beacon.Core.Models;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// The process-wide cap on embed renders running at once (<c>Beacon:Embed:MaxConcurrentRenders</c>), over every grant: the
/// anonymous render route never queues work behind the data sources, it refuses the one render too many at once.
/// </summary>
internal sealed class EmbedRenderLimiter(IOptions<EmbedOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentRenders, options.Value.MaxConcurrentRenders);

    /// <summary>A slot held until the lease is disposed, or null when every slot is taken.</summary>
    public IDisposable? TryAcquire()
    {
        return _slots.Wait(0) ? new Lease(_slots) : null;
    }

    public void Dispose() => _slots.Dispose();

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}

/// <summary>Every embed render slot is taken: the render is refused at once, to be retried.</summary>
internal sealed class EmbedBusyException() : BeaconException("Embed renders are busy; retry shortly.");
