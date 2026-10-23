namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// A pool of concurrency slots whose size can change while slots are held. Growing it frees the extra slots at once (a
/// waiting request takes one); shrinking it takes the free slots out and keeps the ones held when the slots are returned,
/// until the pool is down to its new size, so a held slot is never taken back from running work.
/// </summary>
internal sealed class LlmSlotPool
{
    private readonly SemaphoreSlim _free;
    private readonly object _lock = new();
    private int _capacity;

    // Slots still to take out of the pool after it shrank while they were held: each one returned is kept, not freed.
    private int _owed;

    public LlmSlotPool(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _free = new SemaphoreSlim(capacity, int.MaxValue);
    }

    /// <summary>Takes a free slot without waiting; false when none is free.</summary>
    public bool TryTake()
    {
        return _free.Wait(0);
    }

    public Task TakeAsync(CancellationToken cancellationToken)
    {
        return _free.WaitAsync(cancellationToken);
    }

    public void Return()
    {
        lock (_lock)
        {
            if (_owed > 0)
            {
                _owed--;
                return;
            }

            _free.Release();
        }
    }

    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_lock)
        {
            var delta = capacity - _capacity;
            _capacity = capacity;
            if (delta > 0)
            {
                var forgiven = Math.Min(_owed, delta);
                _owed -= forgiven;
                if (delta > forgiven)
                {
                    _free.Release(delta - forgiven);
                }

                return;
            }

            _owed -= delta;
            while (_owed > 0 && _free.Wait(0))
            {
                _owed--;
            }
        }
    }
}
