using Beacon.Core.Services;
using Warp.Core.Handlers;
using Warp.Core.Concurrency;
using Warp.Core.Timeout;

namespace Beacon.SampleProject.Warp.Jobs;

// Re-encrypts with Beacon:EncryptionKey, in the current format, every stored value only a key in
// Beacon:PreviousEncryptionKeys decrypts or still in the legacy v1 format. Idempotent; enqueued once at every start
// (Program.cs). It logs how many values were still under a previous key or in the legacy format, and fails (Warp shows it)
// if a value could not be written.
// Skip, not Wait: a second run while one holds the key is dropped (Wait would requeue it every polling tick). Safe because
// the running job covers the same values and the next boot enqueues it again. Timeout mode Fail keeps a timed-out run in /warp.
[Timeout(3600, Mode = TimeoutMode.Fail)]
[Mutex("reencrypt-stored-values", Mode = ConcurrencyMode.Skip)]
public sealed class ReEncryptStoredValuesJob : IJob;

public sealed class ReEncryptStoredValuesJobHandler(IStoredValueReEncryptionService reEncryption)
    : IJobHandler<ReEncryptStoredValuesJob>
{
    public Task HandleAsync(ReEncryptStoredValuesJob message, CancellationToken cancellationToken)
        => reEncryption.ReEncryptAsync(cancellationToken);
}
