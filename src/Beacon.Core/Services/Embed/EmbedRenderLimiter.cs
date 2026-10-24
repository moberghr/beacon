using Beacon.Core.Models;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// The caps on embed renders running at once in this process: <c>Beacon:Embed:MaxConcurrentRenders</c> over every grant, and
/// <c>Beacon:Embed:MaxConcurrentRendersPerGrant</c> (a quarter of the total unless set) for any one grant, so one grant's
/// viewers cannot hold every slot. The anonymous render route never queues work behind the data sources: it refuses the one
/// render too many at once.
/// </summary>
internal sealed class EmbedRenderLimiter(IOptions<EmbedOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentRenders, options.Value.MaxConcurrentRenders);
    private readonly int _perGrant = options.Value.EffectiveMaxConcurrentRendersPerGrant;
    private readonly Dictionary<int, int> _inFlight = [];
    private readonly object _sync = new();

    /// <summary>A slot for <paramref name="grantId"/>, held until the lease is disposed, or null when either cap is reached.</summary>
    public IDisposable? TryAcquire(int grantId)
    {
        lock (_sync)
        {
            _inFlight.TryGetValue(grantId, out var current);
            if (current >= _perGrant)
            {
                return null;
            }

            _inFlight[grantId] = current + 1;
        }

        if (_slots.Wait(0))
        {
            return new Lease(this, grantId);
        }

        ReleaseGrant(grantId);

        return null;
    }

    /// <summary>The renders <paramref name="grantId"/> has running in this process.</summary>
    internal int InFlight(int grantId)
    {
        lock (_sync)
        {
            return _inFlight.TryGetValue(grantId, out var current) ? current : 0;
        }
    }

    public void Dispose() => _slots.Dispose();

    private void ReleaseGrant(int grantId)
    {
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(grantId, out var current))
            {
                return;
            }

            // Idle grants leave the map, so it holds only grants with renders running.
            if (current <= 1)
            {
                _inFlight.Remove(grantId);
                return;
            }

            _inFlight[grantId] = current - 1;
        }
    }

    private sealed class Lease(EmbedRenderLimiter owner, int grantId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._slots.Release();
                owner.ReleaseGrant(grantId);
            }
        }
    }
}

/// <summary>Every embed render slot is taken: the render is refused at once, to be retried.</summary>
internal sealed class EmbedBusyException() : BeaconException("Embed renders are busy; retry shortly.");
