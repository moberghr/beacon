namespace Beacon.Core.Services;

/// <summary>
/// One mutual-exclusion lock per key that exists only while a caller holds or waits for it, so the number of
/// entries never exceeds the number of concurrent callers, whatever keys they send.
/// </summary>
internal sealed class KeyedRefreshLocks
{
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal async Task<Releaser> AcquireAsync(int key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(int key, Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    internal sealed class Releaser : IDisposable
    {
        private readonly KeyedRefreshLocks _owner;
        private readonly int _key;
        private readonly Entry _entry;
        private int _released;

        internal Releaser(KeyedRefreshLocks owner, int key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            _entry.Semaphore.Release();
            _owner.Leave(_key, _entry);
        }
    }
}
