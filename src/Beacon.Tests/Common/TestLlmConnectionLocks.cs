using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace Beacon.Tests.Common;

/// <summary>
/// The LLM connection lock for tests that do not race: it never waits, records each time it is taken, and refuses to be
/// taken outside a transaction, as the database lock is owned by one.
/// </summary>
internal sealed class RecordingLlmConnectionLock : ILlmConnectionLock
{
    public int Acquired { get; private set; }

    public Task AcquireAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction == null)
        {
            throw new InvalidOperationException("The LLM connection lock was taken outside a transaction.");
        }

        Acquired++;

        return Task.CompletedTask;
    }
}

/// <summary>
/// The LLM connection lock as the database holds it, over the stand-in transactions of a <see cref="SqlCapture"/>: one
/// holder at a time, held until the holder's transaction ends. A second taker waits (<see cref="Waiting"/> completes); once
/// the lock is free it goes on only when the test calls <see cref="Resume"/>, so the holder's own work after its
/// transaction never runs at the same time as the waiter's.
/// </summary>
internal sealed class SerializingLlmConnectionLock : ILlmConnectionLock
{
    private readonly SemaphoreSlim _held = new(1, 1);
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a taker finds the lock held and waits for it.</summary>
    public Task Waiting => _waiting.Task;

    /// <summary>How many times the lock was granted.</summary>
    public int Acquired { get; private set; }

    public async Task AcquireAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The LLM connection lock was taken outside a transaction.");
        if (!_held.Wait(0))
        {
            _waiting.TrySetResult();

            // Never reentrant, as a second take by the holder would wait for itself: a test that does so fails, never hangs.
            if (!await _held.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new TimeoutException("The LLM connection lock was not released.");
            }

            await _resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        Acquired++;
        SqlCapture.WhenEnded(transaction, () => _held.Release());
    }

    /// <summary>Lets a taker that waited go on once the lock is free.</summary>
    public void Resume()
    {
        _resumed.TrySetResult();
    }
}
